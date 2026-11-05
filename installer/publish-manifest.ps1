<#
.SYNOPSIS
    生成并签名更新清单（latest.json + latest.json.sig），可选推送到 GitHub Pages。

.DESCRIPTION
    自更新的安装包要被执行，"这包是不是官方的"只能靠校验值说话。校验值以前只有
    GitHub API 通道给得出，而 API 有匿名配额（60 次/小时/IP），打满就走 HTML 回落 ——
    那条通道没有校验值，于是出现过"零校验直接执行"（审计第 10 条）。

    清单把这件事拆开：latest.json 里写"版本 + sha256 + 大小 + 下载地址"，
    latest.json.sig 是对它的签名（ECDSA P-256，公钥内置在应用里）。
    于是清单从哪取都无所谓（验签不过就丢那个源），但里面的 sha256 可以当凭据用。

    ⚠ 本脚本必须在<b>上传安装包的那台机器上</b>跑：清单里的 sha256 必须和真正
      发布出去的那个 exe 是同一份字节。CI 里另 build 一份再签 = 签的是另一个文件。

.PARAMETER SetupExe
    要发布的安装包。省略时自动找 dist-tmp 里最新的 JuniGrid-cn-v*-setup.exe。

.PARAMETER Version
    版本号。省略时从文件名里抠。

.PARAMETER OutDir
    清单输出目录。默认与安装包同目录。

.PARAMETER KeyPath
    签名私钥（PKCS#8 PEM）。默认读环境变量 JUNIGRID_MANIFEST_KEY，
    都没有就用 E:\junigrid\keys\junigrid-manifest-2026-01.pem。

.PARAMETER Publish
    额外把两个文件推到 GitHub Pages（gh-pages 分支）。需要 gh CLI 已登录。

.EXAMPLE
    .\installer\publish-manifest.ps1
    .\installer\publish-manifest.ps1 -Publish
#>
[CmdletBinding()]
param(
    [string] $SetupExe,
    [string] $Version,
    [string] $OutDir,
    [string] $KeyPath,
    [switch] $Publish
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

# ── 定位安装包 ────────────────────────────────────────────────────────────────
if (-not $SetupExe) {
    $candidates = @()
    foreach ($dir in @((Join-Path $repo 'dist-tmp'), (Split-Path -Parent $repo))) {
        if (Test-Path $dir) {
            $candidates += Get-ChildItem -Path $dir -Filter 'JuniGrid-cn-v*-setup.exe' -File -ErrorAction SilentlyContinue
        }
    }
    if ($candidates.Count -eq 0) { throw "找不到安装包：dist-tmp 和桌面都没有 JuniGrid-cn-v*-setup.exe" }
    $SetupExe = ($candidates | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
}
if (-not (Test-Path $SetupExe)) { throw "安装包不存在：$SetupExe" }
$SetupExe = (Resolve-Path $SetupExe).Path

if (-not $Version) {
    $m = [regex]::Match([IO.Path]::GetFileName($SetupExe), 'v(\d+\.\d+\.\d+)')
    if (-not $m.Success) { throw "从文件名里抠不出版本号，请用 -Version 指定" }
    $Version = $m.Groups[1].Value
}

if (-not $OutDir) { $OutDir = Split-Path -Parent $SetupExe }
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Force $OutDir | Out-Null }

# ── 定位私钥 ──────────────────────────────────────────────────────────────────
if (-not $KeyPath) { $KeyPath = $env:JUNIGRID_MANIFEST_KEY }
if (-not $KeyPath) { $KeyPath = 'E:\junigrid\keys\junigrid-manifest-2026-01.pem' }
if (-not (Test-Path $KeyPath)) {
    throw @"
找不到签名私钥：$KeyPath
  · 先用 tools/JuniGrid.ManifestSigner keygen 生成一对
  · 私钥放仓库外，并离线备份两份 —— 丢了就再也发不了新版
"@
}

Write-Host "安装包：$SetupExe"
Write-Host "版本  ：$Version"
Write-Host "私钥  ：$KeyPath"
Write-Host ""

# ── 生成 + 签名 + 自检 ────────────────────────────────────────────────────────
$tool = Join-Path $repo 'tools\JuniGrid.ManifestSigner'
& dotnet run --project $tool -c Release -- make --exe $SetupExe --version $Version --outdir $OutDir --key $KeyPath
if ($LASTEXITCODE -ne 0) { throw "生成清单失败（退出码 $LASTEXITCODE）" }

& dotnet run --project $tool -c Release -- verify --manifest (Join-Path $OutDir 'latest.json') --sig (Join-Path $OutDir 'latest.json.sig')
if ($LASTEXITCODE -ne 0) { throw "清单自检不过 —— 别发布" }

# ── 可选：推到 GitHub Pages ───────────────────────────────────────────────────
if ($Publish) {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw "要用 -Publish 就得有 gh CLI" }
    $repoSlug = 'MLD-yu/JuniGrid'
    $branch = 'gh-pages'

    # 分支不存在就建一个（从 main 的 HEAD 起）
    $null = gh api "repos/$repoSlug/git/refs/heads/$branch" 2>$null
    if ($LASTEXITCODE -ne 0) {
        $head = (gh api "repos/$repoSlug/git/ref/heads/main" | ConvertFrom-Json).object.sha
        $null = gh api --method POST "repos/$repoSlug/git/refs" -f "ref=refs/heads/$branch" -f "sha=$head"
        if ($LASTEXITCODE -ne 0) { throw "建 $branch 分支失败" }
        Write-Host "已创建 $branch 分支"
    }

    foreach ($name in @('latest.json', 'latest.json.sig')) {
        $path = Join-Path $OutDir $name
        $b64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($path))
        $null = gh api --method PUT "repos/$repoSlug/contents/$name" `
            -f "branch=$branch" -f "message=更新 $name（$Version）" -f "content=$b64"
        if ($LASTEXITCODE -ne 0) { throw "推送 $name 失败" }
        Write-Host "已推送 $name → $branch"
    }
    Write-Host ""
    Write-Host "清单地址：https://mld-yu.github.io/JuniGrid/latest.json"
}

Write-Host ""
Write-Host "接下来：把安装包本体传到 GitHub Release（清单里的 url 指向那个下载地址）。"
Write-Host "注意：清单里的 sha256 必须和 Release 上那个 exe 是同一份字节 —— 所以要先传 exe，再跑本脚本。"
