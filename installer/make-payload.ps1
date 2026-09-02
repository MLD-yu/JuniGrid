# 把 publish\sc（主程序 self-contained 输出）压成安装器内嵌的 payload.lz
# （JGP1 容器 + 单流 LZMA 固实压缩，比逐文件 Deflate 的旧 payload.zip 小 25~30%）
$ErrorActionPreference = 'Stop'
$installerDir = $PSScriptRoot
$projectDir   = Join-Path $installerDir 'JuniGridInstaller'
$repo         = Split-Path -Parent $installerDir
$sc           = Join-Path $repo 'publish\sc'

if (-not (Test-Path (Join-Path $sc 'JuniGrid.exe'))) {
    Write-Host "publish\sc 不存在，先发布主程序（self-contained）…"
    dotnet publish (Join-Path $repo 'JuniGrid') -c Release -r win-x64 --self-contained true -p:DebugType=none -o $sc
    if ($LASTEXITCODE -ne 0) { throw "主程序发布失败" }
}

$payload = Join-Path $projectDir 'payload.lz'
Remove-Item $payload -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $projectDir 'payload.zip') -Force -ErrorAction SilentlyContinue

# PayloadTool：压缩后自动全量解压比对 SHA-256，哈希不符直接抛错
dotnet run --project (Join-Path $installerDir 'PayloadTool') -c Release -- c $sc $payload
if ($LASTEXITCODE -ne 0) { throw "payload 压缩/校验失败" }

Write-Host ("payload.lz: {0:N1} MB" -f ((Get-Item $payload).Length / 1MB))
