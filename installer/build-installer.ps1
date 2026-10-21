# 一键产出 Riot 风格自研安装器：dist-tmp\JuniGrid-cn-v<版本>-setup.exe
# 用法: powershell -File build-installer.ps1 [-SkipAppPublish] [-SkipGuards]
#   -SkipAppPublish  复用现有 publish\sc，不重新发布主程序（日常打安装器用）
#   -SkipGuards      跳过 5 个回归防线脚本。只给 CI 用 —— 那几条要起真实进程、
#                    操真实窗口，GitHub Actions 的无桌面会话跑不了；本地发版必须跑全。
$param = $args
$ErrorActionPreference = 'Stop'
$installerDir = $PSScriptRoot
$repo = Split-Path -Parent $installerDir

# 1) 版本号单一来源：JuniGrid\JuniGrid.csproj 的 <Version>
$csproj = Join-Path $repo 'JuniGrid\JuniGrid.csproj'
$version = [regex]::Match((Get-Content $csproj -Raw), '<Version>([^<]+)</Version>').Groups[1].Value
if (-not $version) { throw "Cannot read version from JuniGrid.csproj" }
Write-Host "=== JuniGrid Installer v$version ==="

$skipPublish = $param -contains '-SkipAppPublish'
$skipGuards = $param -contains '-SkipGuards'

# 2) Main app self-contained publish
$sc = Join-Path $repo 'publish\sc'
if (-not $skipPublish -or -not (Test-Path (Join-Path $sc 'JuniGrid.exe'))) {
    dotnet publish (Join-Path $repo 'JuniGrid\JuniGrid.csproj') -c Release -r win-x64 --self-contained true -p:DebugType=none -o $sc
    if ($LASTEXITCODE -ne 0) { throw "App publish failed" }
}

# 3) Bundle offline deps into payload: DepotDownloader + WebView2 + XNA
$deps = Join-Path $repo 'installer\deps'
$tools = Join-Path $sc 'tools\DepotDownloader'
if (-not (Test-Path (Join-Path $tools 'DepotDownloader.exe'))) {
    New-Item -ItemType Directory -Force $tools | Out-Null
    # Prefer trimmed self-contained (~12MB); then framework (~2.5MB, needs .NET 9+); then full (~32MB zip)
    $ddTrimmed = Join-Path $deps 'DepotDownloader-win-x64-trimmed.zip'
    $ddFramework = Join-Path $deps 'DepotDownloader-framework.zip'
    $ddFull = Join-Path $deps 'DepotDownloader-windows-x64.zip'
    if (Test-Path $ddTrimmed) {
        Expand-Archive -Path $ddTrimmed -DestinationPath $tools -Force
        Write-Host "Bundled DepotDownloader trimmed (~12MB, offline)"
    } elseif (Test-Path $ddFramework) {
        Expand-Archive -Path $ddFramework -DestinationPath $tools -Force
        Write-Host "Bundled DepotDownloader framework (~8MB, needs .NET 9+)"
    } elseif (Test-Path $ddFull) {
        Expand-Archive -Path $ddFull -DestinationPath $tools -Force
        Write-Host "Bundled DepotDownloader full self-contained (~78MB)"
    } else {
        Write-Host "NOTE: no DepotDownloader package in installer\deps; will download at runtime"
    }
}
$wv2Dst = Join-Path $sc 'tools\WebView2'
if (-not (Test-Path (Join-Path $wv2Dst 'MicrosoftEdgeWebView2Setup.exe'))) {
    New-Item -ItemType Directory -Force $wv2Dst | Out-Null
    $wv2Src = Join-Path $deps 'MicrosoftEdgeWebView2Setup.exe'
    if (Test-Path $wv2Src) {
        Copy-Item $wv2Src $wv2Dst -Force
        Write-Host "Bundled WebView2 bootstrapper"
    } else {
        Write-Host "NOTE: MicrosoftEdgeWebView2Setup.exe missing in installer\deps"
    }
}
$xnaDst = Join-Path $sc 'tools\XnaRedist'
if (-not (Test-Path (Join-Path $xnaDst 'xnafx40_redist.msi'))) {
    New-Item -ItemType Directory -Force $xnaDst | Out-Null
    $xnaSrc = Join-Path $deps 'xnafx40_redist.msi'
    if (Test-Path $xnaSrc) {
        Copy-Item $xnaSrc $xnaDst -Force
        Write-Host "Bundled XNA 4.0 redist"
    }
}

# 4) Make payload.lz (JGP1 + solid LZMA, full SHA-256 verify)
& (Join-Path $installerDir 'make-payload.ps1')

# 5) Installer single-file publish (embeds payload.lz)
# Must IncludeNativeLibrariesForSelfExtract=true or WPF native libs fail to load.
# Do not run the compressed bundle from a OneDrive-synced folder.
$out = Join-Path $installerDir 'publish'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish (Join-Path $installerDir 'JuniGridInstaller') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none -p:Version=$version -o $out
if ($LASTEXITCODE -ne 0) { throw "Installer publish failed" }

# 5b) 回归防线：M10 搬家清单漂移 / M11 回收调用链形状 / RT* 守卫行为 / 搬家·配置·nxm 三套夹具
#     / SI* 单实例闸的可见出口（真起两个实例看旧窗口有没有被拉回屏上）
#     必须跑在 publish 之后、拷进 dist-tmp 之前：
#       · RT* 要跑真实入口，用的是刚 publish 出来的 exe（早一步跑就是在验上一版）
#       · 拷进 dist-tmp 就等于发出去了，缺陷不能带着走
$guards = @(
    (Join-Path $installerDir 'jg-installer-drift.py'),
    (Join-Path $installerDir 'jg-migrate-verify.py'),
    (Join-Path $installerDir 'jg-oneclick-verify.py'),
    (Join-Path $installerDir 'jg-nxm-verify.py'),
    (Join-Path $installerDir 'jg-single-instance-verify.py')
)
$probe = Join-Path $out 'JuniGridSetup.exe'
$env:PYTHONIOENCODING = "utf-8"   # 防线脚本要打印中文，控制台默认 GBK 会把 print 打崩
if ($skipGuards) {
    # CI 用。这几条要起真实进程 / 操真实窗口，无桌面会话的环境必然失败；
    # 但本地发版绝不能用这个开关 —— 防线就是发版前最后一道闸。
    Write-Host "=== 已跳过安装器回归防线（-SkipGuards）—— 此产物不得直接发布 ==="
} else {
foreach ($guard in $guards) {
    if (-not (Test-Path $guard)) {
        throw "缺少回归防线脚本 $guard —— 防线空转比没有防线更危险，打包中止"
    }
    Write-Host "=== 安装器回归防线: $(Split-Path $guard -Leaf) ==="
    if (Test-Path $probe) {
        python $guard --exe $probe
    } else {
        Write-Host "NOTE: 未找到 $probe"
        python $guard
    }
    if ($LASTEXITCODE -ne 0) { throw "安装器回归防线未通过（$(Split-Path $guard -Leaf)），已中止拷贝到 dist-tmp" }
}
}

# 6) Copy to dist-tmp (name matches SelfUpdateService expectations)
$dist = Join-Path $repo 'dist-tmp'
New-Item -ItemType Directory -Force $dist | Out-Null
$dest = Join-Path $dist ("JuniGrid-cn-v{0}-setup.exe" -f $version)
Copy-Item (Join-Path $out 'JuniGridSetup.exe') $dest -Force
Write-Host ("Output: {0}  ({1:N1} MB)" -f $dest, ((Get-Item $dest).Length / 1MB))

# 7) 微软 SmartScreen 申诉材料。未签名文件的信誉绑在哈希上，每次构建哈希都变 ⇒ 每次发版
#    都得重新提一次。这里只把"填表要抄的东西"生成好；提交本身得人来做（要用他的账号，
#    而且那是对微软发出的对外动作）。放在防线之后：防线没过就走不到这儿，不会生成一份
#    指向坏产物的申诉材料。
$sha = (Get-FileHash $dest -Algorithm SHA256).Hash.ToLower()
$size = (Get-Item $dest).Length
$note = Get-Content (Join-Path $installerDir 'smartscreen-submission-note.txt') -Raw
$note = $note.Replace('{VERSION}', $version)
$note = $note.Replace('{FILE}', (Split-Path $dest -Leaf))
$note = $note.Replace('{SHA}', $sha)
$note = $note.Replace('{SIZE}', "$size")
$noteOut = Join-Path $dist ("jg-smartscreen-submission-v{0}.txt" -f $version)
Set-Content -Path $noteOut -Value $note -Encoding ASCII -NoNewline
Write-Host ("SHA-256 : {0}" -f $sha)
Write-Host ("Size    : {0} bytes" -f $size)
Write-Host ("申诉材料 : {0}  （表单里「产品」一栏要选 Microsoft Defender SmartScreen，不是反病毒）" -f $noteOut)