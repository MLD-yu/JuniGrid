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
    都没有就用 %USERPROFILE%\.junigrid\keys\junigrid-manifest-2026-01.pem。
    （别放在任何缓存目录里：缓存会被清理/搬家，钥匙跟着走就没了。）

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
    [switch] $Publish,
    [switch] $SkipUrlCheck
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
if (-not $KeyPath) { $KeyPath = Join-Path $env:USERPROFILE '.junigrid\keys\junigrid-manifest-2026-01.pem' }
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

# ── 发布前置：清单里的下载地址必须真的能下到同一份字节 ───────────────────────
# 先发清单、后发 release 的后果：所有拿到清单的用户下载 404，再退回老路 —— 不投毒，
# 但白折腾一轮，而且清单在 Pages 上会被 CDN 缓存一会儿。所以这里硬拦。
if (-not $SkipUrlCheck) {
    $doc = Get-Content -LiteralPath (Join-Path $OutDir 'latest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $url = [string]$doc.url
    $ok = $false
    $remoteLen = $null
    try {
        $resp = Invoke-WebRequest -Uri $url -Method Head -MaximumRedirection 10 -TimeoutSec 20 -UseBasicParsing
        if ($resp.StatusCode -ge 200 -and $resp.StatusCode -lt 300) {
            $ok = $true
            if ($resp.Headers['Content-Length']) { $remoteLen = [int64]$resp.Headers['Content-Length'] }
        }
    } catch {
        # 有些 CDN 拒 HEAD（405）—— 用 Range GET 兜底，206 就说明文件在
        try {
            $resp = Invoke-WebRequest -Uri $url -Headers @{ Range = 'bytes=0-0' } -TimeoutSec 20 -UseBasicParsing
            if ($resp.StatusCode -eq 206) { $ok = $true }
        } catch { }
    }
    if (-not $ok) {
        throw "清单里的下载地址取不到：$url`n先把安装包传到 GitHub Release（同一个版本号），再跑本脚本。"
    }
    if ($remoteLen -and $remoteLen -ne [int64]$doc.size) {
        throw "Release 上的文件是 $remoteLen 字节，本地这份是 $($doc.size) 字节 —— 不是同一份构建，不能签。"
    }
    Write-Host "发布前置通过：$url（$($doc.size) 字节）"
}

# ── 可选：推到 GitHub Pages ───────────────────────────────────────────────────
if ($Publish) {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw "要用 -Publish 就得有 gh CLI" }
    $repoSlug = 'MLD-yu/JuniGrid'
    $branch = 'gh-pages'
    # gh 从 stdin 读的 JSON 按 UTF-8；PS 5.1 默认按控制台代码页，中文提交信息会乱码
    $OutputEncoding = New-Object System.Text.UTF8Encoding($false)

    function Invoke-GhJson([string]$method, [string]$path, $obj) {
        $body = $obj | ConvertTo-Json -Depth 10 -Compress
        $out = $body | gh api --method $method $path --input -
        if ($LASTEXITCODE -ne 0) { throw "gh api $method $path 失败" }
        if (-not $out) { return $null }
        return ($out | ConvertFrom-Json)
    }

    $branchExists = $true
    $null = gh api "repos/$repoSlug/git/ref/heads/$branch" 2>$null
    if ($LASTEXITCODE -ne 0) { $branchExists = $false }

    if (-not $branchExists) {
        # 建「孤儿」分支：只装清单两个文件，不要把整个源码树带进 Pages
        $jsonB64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $OutDir 'latest.json')))
        $sigB64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $OutDir 'latest.json.sig')))
        $bJson = Invoke-GhJson POST "repos/$repoSlug/git/blobs" @{ content = $jsonB64; encoding = 'base64' }
        $bSig = Invoke-GhJson POST "repos/$repoSlug/git/blobs" @{ content = $sigB64; encoding = 'base64' }
        $bNoJekyll = Invoke-GhJson POST "repos/$repoSlug/git/blobs" @{ content = ''; encoding = 'utf-8' }
        $bIndex = Invoke-GhJson POST "repos/$repoSlug/git/blobs" @{ content = "JuniGrid update manifest`n"; encoding = 'utf-8' }
        $tree = Invoke-GhJson POST "repos/$repoSlug/git/trees" @{ tree = @(
                @{ path = 'latest.json'; mode = '100644'; type = 'blob'; sha = $bJson.sha },
                @{ path = 'latest.json.sig'; mode = '100644'; type = 'blob'; sha = $bSig.sha },
                @{ path = '.nojekyll'; mode = '100644'; type = 'blob'; sha = $bNoJekyll.sha },
                @{ path = 'index.html'; mode = '100644'; type = 'blob'; sha = $bIndex.sha })
        }
        $commit = Invoke-GhJson POST "repos/$repoSlug/git/commits" @{ message = "manifest: v$Version"; tree = $tree.sha }
        $null = Invoke-GhJson POST "repos/$repoSlug/git/refs" @{ ref = "refs/heads/$branch"; sha = $commit.sha }
        Write-Host "已创建 $branch 分支（孤儿分支，只含清单文件）"
    }
    else {
        # 文件已存在时 contents API 必须带现有 blob 的 sha，否则 422「sha wasn't supplied」。
        # 不取这一步的话，第一次发布能过、第二次必失败。
        foreach ($name in @('latest.json', 'latest.json.sig')) {
            $b64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $OutDir $name)))
            $sha = $null
            $existing = gh api "repos/$repoSlug/contents/$name`?ref=$branch" 2>$null
            if ($LASTEXITCODE -eq 0 -and $existing) { $sha = ($existing | ConvertFrom-Json).sha }
            $fields = @{ branch = $branch; message = "update $name (v$Version)"; content = $b64 }
            if ($sha) { $fields['sha'] = $sha }
            $null = Invoke-GhJson PUT "repos/$repoSlug/contents/$name" $fields
            Write-Host ("已推送 {0} → {1}{2}" -f $name, $branch, $(if ($sha) { '（更新）' } else { '（新增）' }))
        }
    }

    # 打开 Pages（源 = gh-pages/）。已开就跳过 —— 只推文件不开开关，站点永远 404。
    $null = gh api "repos/$repoSlug/pages" 2>$null
    if ($LASTEXITCODE -ne 0) {
        $null = Invoke-GhJson POST "repos/$repoSlug/pages" @{ source = @{ branch = $branch; path = '/' } }
        Write-Host "已打开 GitHub Pages（源 = $branch）"
    }

    Write-Host "清单地址：https://mld-yu.github.io/JuniGrid/latest.json"

    # 站点首次发布要等构建。等线上那份真的等于我们刚签的这份，再说"发布完成"。
    $local = Get-Content -LiteralPath (Join-Path $OutDir 'latest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $servedOk = $false
    for ($i = 0; $i -lt 30; $i++) {
        try {
            $gr = Invoke-RestMethod -Uri 'https://mld-yu.github.io/JuniGrid/latest.json' -TimeoutSec 10
            if ($gr.version -eq $local.version -and $gr.sha256 -eq $local.sha256) { $servedOk = $true; break }
        } catch { }
        Start-Sleep -Seconds 3
    }
    if ($servedOk) { Write-Host "线上已生效：v$($local.version)" }
    else { Write-Host "⚠ 线上还没生效（Pages 可能仍在构建）—— 过一两分钟访问上面的地址确认" }
}

Write-Host ""
Write-Host "接下来：把安装包本体传到 GitHub Release（清单里的 url 指向那个下载地址）。"
Write-Host "注意：清单里的 sha256 必须和 Release 上那个 exe 是同一份字节 —— 所以要先传 exe，再跑本脚本。"
