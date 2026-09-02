# 一键产出 Riot 风格自研安装器：dist-tmp\JuniGrid-cn-v<版本>-setup.exe
# 用法: powershell -File build-installer.ps1 [-SkipAppPublish]
#   -SkipAppPublish  复用现有 publish\sc，不重新发布主程序（日常打安装器用）
$param = $args
$ErrorActionPreference = 'Stop'
$installerDir = $PSScriptRoot
$repo = Split-Path -Parent $installerDir

# 1) 版本号单一来源：JuniGrid\Services\AppInfo.cs
$version = [regex]::Match((Get-Content (Join-Path $repo 'JuniGrid\Services\AppInfo.cs') -Raw),
    'Version\s*=\s*"([^"]+)"').Groups[1].Value
if (-not $version) { throw "无法从 AppInfo.cs 读取版本号" }
Write-Host "=== JuniGrid 安装器 v$version ==="

$skipPublish = $param -contains '-SkipAppPublish'

# 2) 主程序 self-contained 发布
$sc = Join-Path $repo 'publish\sc'
if (-not $skipPublish -or -not (Test-Path (Join-Path $sc 'JuniGrid.exe'))) {
    dotnet publish (Join-Path $repo 'JuniGrid') -c Release -r win-x64 --self-contained true -p:DebugType=none -o $sc
    if ($LASTEXITCODE -ne 0) { throw "主程序发布失败" }
}

# 3) 制作 payload.lz（LZMA 固实容器，含全量 SHA-256 校验）
& (Join-Path $installerDir 'make-payload.ps1')

# 4) 安装器单文件发布（内嵌 payload.lz）
# 注意: 必须带 IncludeNativeLibrariesForSelfExtract=true——压缩 bundle 下 WPF 原生库
#       需要自解压才能加载，否则启动即 DllNotFoundException。
# 注意: 压缩 bundle 与 OneDrive 同步目录冲突——安装包别放在 OneDrive 目录里直接运行，
#       下载到本地（如 Downloads）后正常运行。
$out = Join-Path $installerDir 'publish'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish (Join-Path $installerDir 'JuniGridInstaller') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none -p:Version=$version -o $out
if ($LASTEXITCODE -ne 0) { throw "安装器发布失败" }

# 5) 复制到 dist-tmp（命名与旧 Inno 产物一致，SelfUpdateService 无需改动）
$dist = Join-Path $repo 'dist-tmp'
New-Item -ItemType Directory -Force $dist | Out-Null
$dest = Join-Path $dist ("JuniGrid-cn-v{0}-setup.exe" -f $version)
Copy-Item (Join-Path $out 'JuniGridSetup.exe') $dest -Force
Write-Host ("产物: {0}  ({1:N1} MB)" -f $dest, ((Get-Item $dest).Length / 1MB))
