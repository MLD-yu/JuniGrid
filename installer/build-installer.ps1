# 一键产出 Riot 风格自研安装器：dist-tmp\JuniGrid-cn-v<版本>-setup.exe
# 用法: powershell -File build-installer.ps1 [-SkipAppPublish]
#   -SkipAppPublish  复用现有 publish\sc，不重新发布主程序（日常打安装器用）
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

# 6) Copy to dist-tmp (name matches SelfUpdateService expectations)
$dist = Join-Path $repo 'dist-tmp'
New-Item -ItemType Directory -Force $dist | Out-Null
$dest = Join-Path $dist ("JuniGrid-cn-v{0}-setup.exe" -f $version)
Copy-Item (Join-Path $out 'JuniGridSetup.exe') $dest -Force
Write-Host ("Output: {0}  ({1:N1} MB)" -f $dest, ((Get-Item $dest).Length / 1MB))