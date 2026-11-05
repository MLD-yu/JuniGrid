<#
.SYNOPSIS
    恢复演练：验证某份私钥副本是不是"能用的那把"。

.DESCRIPTION
    备份不做恢复演练等于没有 —— 纸上的 PEM 被人折了一下、手抄错一个字符、
    U 盘文件损���，这些都要等真出事那天才发现。这个脚本就是那份"演练"：

      1) 候选私钥能导入（格式对不对）
      2) 它导出的公钥与应用里内置的公钥<b>逐字相同</b>（是不是同一把钥匙）
      3) 用它签一份清单，应用的验签逻辑认（能不能签出应用信任的签名）

    三步全过 = 这份副本可以当备份用。任何一步不过就是废的，别留着当安全感。

.PARAMETER KeyPath
    要验证的私钥（PKCS#8 PEM）。可以是打印稿手抄回来的那份。

.EXAMPLE
    .\installer\verify-key-backup.ps1 -KeyPath "$env:USERPROFILE\.junigrid\keys\junigrid-manifest-2026-01.pem"
    .\installer\verify-key-backup.ps1 -KeyPath "$env:TEMP\restored-from-paper.pem"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $KeyPath,
    [string] $Repo
)

$ErrorActionPreference = 'Stop'
if (-not $Repo) { $Repo = Split-Path -Parent $PSScriptRoot }
$tool = Join-Path $Repo 'tools\JuniGrid.ManifestSigner'
$trustFile = Join-Path $Repo 'JuniGrid.Manifest\ManifestTrust.cs'

Write-Host "候选私钥：$KeyPath"
Write-Host ""

if (-not (Test-Path $KeyPath)) {
    Write-Host "✗ 文件不存在：$KeyPath" -ForegroundColor Red
    exit 1
}

# ── ① 应用内置的公钥（代码里那份，不是私钥推出来的）─────────────────────────
$src = Get-Content -LiteralPath $trustFile -Raw -Encoding UTF8
$m = [regex]::Match($src, 'PublicKeySpkiBase64\s*=\s*"([A-Za-z0-9+/=]+)"')
if (-not $m.Success) { throw "在 $trustFile 里找不到 PublicKeySpkiBase64" }
$embedded = $m.Groups[1].Value
Write-Host "① 应用内置公钥（$($embedded.Length) 字符）：$($embedded.Substring(0, [Math]::Min(24, $embedded.Length)))…"

# ── ② 候选私钥导出的公钥，必须逐字相同 ──────────────────────────────────────
$derived = (& dotnet run --project $tool -c Release -- pubkey --key $KeyPath 2>$null | Select-Object -Last 1)
if ($LASTEXITCODE -ne 0 -or -not $derived) {
    Write-Host "✗ 这份副本读不出私钥（格式不对？被折过/抄错了？）—— 备份不可用" -ForegroundColor Red
    exit 1
}
# PowerShell 5.1 没有 ??（那是 7+ 的语法）；用它会让整份脚本在解析期就死、一行都不跑
if ($null -eq $derived) { $derived = '' }
$derived = $derived.Trim()
if (-not $derived) {
    Write-Host "✗ 私钥没导出公钥 —— 备份不可用" -ForegroundColor Red
    exit 1
}
if ($derived -ne $embedded) {
    Write-Host "✗ 导出的公钥和应用内置的不一致 —— 不是同一把钥匙" -ForegroundColor Red
    Write-Host "  导出：$derived"
    Write-Host "  内置：$embedded"
    exit 1
}
Write-Host "✓ ② 导出的公钥与应用内置的逐字相同" -ForegroundColor Green

# ── ③ 用它签一份清单，应用的验签逻辑要认 ────────────────────────────────────
$tmp = Join-Path $env:TEMP ("jg-keycheck-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $tmp | Out-Null
try {
    $dummy = Join-Path $tmp 'dummy.bin'
    [IO.File]::WriteAllBytes($dummy, (1..4096 | ForEach-Object { [byte]($_ % 251) }))

    & dotnet run --project $tool -c Release -- make --exe $dummy --version 9.9.9 --outdir $tmp --key $KeyPath | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "✗ 用这份副本签名失败 —— 备份不可用" -ForegroundColor Red
        exit 1
    }

    $verifyOut = & dotnet run --project $tool -c Release -- verify --manifest (Join-Path $tmp 'latest.json') --sig (Join-Path $tmp 'latest.json.sig') 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "✗ 签出来的签名应用不认" -ForegroundColor Red
        $verifyOut | ForEach-Object { Write-Host "  $_" }
        exit 1
    }
    Write-Host "✓ ③ 用它签出的签名，应用验签通过" -ForegroundColor Green
}
finally {
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "这份副本可以用。" -ForegroundColor Green
Write-Host "提醒：备份放在本机之外的地方才算备份（异地 / 纸质 / 加密介质）。" -ForegroundColor Yellow
Write-Host "恢复步骤见 backup-sheet.txt 的「恢复演练」一节。"
