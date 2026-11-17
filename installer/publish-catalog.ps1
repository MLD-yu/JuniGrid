<#
.SYNOPSIS
    校验 + 签名游戏版本表（game-versions.json），可选推到 GitHub Pages。

.DESCRIPTION
    这张表是「星露谷版本号 ↔ Steam depot manifest」的对照。以前它写死在
    DepotDownloaderService.cs 里，于是星露谷每更新一次，作者就得改代码、重编、
    发一个 120MB 的安装包，玩家更新完才看得到新版本。

    现在表在 JuniGrid\Assets\game-versions.json：
      · 它同时被编进程序集（离线兜底，断网/首次启动也有表）
      · 签名后发布到静态托管，客户端后台拉取并用内置公钥验签
    于是「加一个游戏版本」= 改一行 json + 跑本脚本，不用发版。

    ⚠ 表里只有版本号和 Steam manifest GID —— 公开元数据，不涉及游戏本体分发。
      游戏文件仍然由用户用自己的 Steam 账号从 Steam CDN 拉。

.PARAMETER File
    版本表路径。默认 JuniGrid\Assets\game-versions.json。

.PARAMETER KeyPath
    签名私钥（PKCS#8 PEM），和更新清单共用同一把。默认读环境变量
    JUNIGRID_MANIFEST_KEY，都没有就用 %USERPROFILE%\.junigrid\keys\junigrid-manifest-2026-01.pem。

.PARAMETER Publish
    额外把 game-versions.json + .sig 推到 gh-pages（需要 gh CLI 已登录，且 Pages 已开）。

.PARAMETER NoStamp
    不自动把 updated_at 改成当前时间（只想校验/签名、不想改文件时用）。

.EXAMPLE
    .\installer\publish-catalog.ps1
    .\installer\publish-catalog.ps1 -Publish

.EXAMPLE
    # 只校验，不动任何文件、不签名
    dotnet run --project tools\JuniGrid.ManifestSigner -- catalog --file JuniGrid\Assets\game-versions.json
#>
[CmdletBinding()]
param(
    [string] $File,
    [string] $KeyPath,
    [switch] $Publish,
    [switch] $NoStamp,
    [string] $RepoSlug = 'MLD-yu/JuniGrid',
    [string] $Branch = 'gh-pages'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

if (-not $File) { $File = Join-Path $repo 'JuniGrid\Assets\game-versions.json' }
if (-not (Test-Path $File)) { throw "找不到版本表：$File" }
$File = (Resolve-Path $File).Path
$sig = "$File.sig"

if (-not $KeyPath) { $KeyPath = $env:JUNIGRID_MANIFEST_KEY }
if (-not $KeyPath) { $KeyPath = Join-Path $env:USERPROFILE '.junigrid\keys\junigrid-manifest-2026-01.pem' }
if (-not (Test-Path $KeyPath)) {
    throw @"
找不到签名私钥：$KeyPath
  · 和更新清单用同一把，见 docs\自更新清单签名.md
  · 私钥在仓库外，离线备份两份 —— 丢了只能换密钥，老客户端不认新公钥
"@
}

$tool = Join-Path $repo 'tools\JuniGrid.ManifestSigner'
Write-Host "版本表：$File"
Write-Host "私钥  ：$KeyPath"
Write-Host ""

# ── 0) 先编译一次工具 ─────────────────────────────────────────────────────────
# 后面三次调用都走 --no-build：dotnet run 每次现编会把 MSBuild 的进度条混进输出里
# （"csproj (0.3 秒)" 几十行），日志读起来像出错。编译一次、跑三次，输出干净。
& dotnet build $tool -c Release -v q --nologo -tl:off
if ($LASTEXITCODE -ne 0) { throw "签名工具编译失败（退出码 $LASTEXITCODE）" }

# ── 1) 结构校验（顺便盖 updated_at）────────────────────────────────────────────
# 先校验再签名：签一份字段写错的表照样"验签通过"，而写错一行的后果是静默的
#（版本比较反了 → 存档读不进 / 该锁的 appmanifest 判成不用锁）。
$catalogArgs = @('run', '--project', $tool, '-c', 'Release', '--no-build', '--', 'catalog', '--file', $File)
if (-not $NoStamp) { $catalogArgs += '--stamp' }
& dotnet @catalogArgs
if ($LASTEXITCODE -ne 0) { throw "版本表不合规（退出码 $LASTEXITCODE）—— 别发布" }

# ── 2) 签名 ───────────────────────────────────────────────────────────────────
& dotnet run --project $tool -c Release --no-build -- sign --manifest $File --key $KeyPath --out $sig
if ($LASTEXITCODE -ne 0) { throw "签名失败（退出码 $LASTEXITCODE）" }

# ── 3) 复检：结构 + 签名一起过一遍 ────────────────────────────────────────────
& dotnet run --project $tool -c Release --no-build -- catalog --file $File --sig $sig
if ($LASTEXITCODE -ne 0) { throw "复检不过 —— 别发布" }

# ── 4) 可选：推到 GitHub Pages ────────────────────────────────────────────────
if ($Publish) {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw "要用 -Publish 就得有 gh CLI" }
    # gh 从 stdin 读的 JSON 按 UTF-8；PS 5.1 默认按控制台代码页，中文提交信息会乱码
    $OutputEncoding = New-Object System.Text.UTF8Encoding($false)

    $null = gh api "repos/$RepoSlug/git/ref/heads/$Branch" 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "远程没有 $Branch 分支 —— 先跑一次 installer\publish-manifest.ps1 -Publish，把 Pages 建起来"
    }

    foreach ($name in @('game-versions.json', 'game-versions.json.sig')) {
        $local = if ($name.EndsWith('.sig')) { $sig } else { $File }
        $b64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($local))

        # 文件已存在时 contents API 必须带现有 blob 的 sha，否则 422「sha wasn't supplied」。
        $sha = $null
        $existing = gh api "repos/$RepoSlug/contents/$name`?ref=$Branch" 2>$null
        if ($LASTEXITCODE -eq 0 -and $existing) { $sha = ($existing | ConvertFrom-Json).sha }

        $fields = @{
            branch  = $Branch
            message = "catalog: 游戏版本表 $(Get-Date -Format 'yyyy-MM-dd HH:mm')"
            content = $b64
        }
        if ($sha) { $fields['sha'] = $sha }

        $body = $fields | ConvertTo-Json -Depth 5 -Compress
        $null = $body | gh api --method PUT "repos/$RepoSlug/contents/$name" --input -
        if ($LASTEXITCODE -ne 0) { throw "推送 $name 失败" }
        Write-Host ("已推送 {0} → {1}{2}" -f $name, $Branch, $(if ($sha) { '（更新）' } else { '（新增）' }))
    }

    Write-Host "版本表地址：https://mld-yu.github.io/JuniGrid/game-versions.json"

    # 等线上那份真的等于本地这份再说"发布完成"（Pages 构建有延迟）
    $localDoc = Get-Content -LiteralPath $File -Raw -Encoding UTF8 | ConvertFrom-Json
    $served = $false
    for ($i = 0; $i -lt 30; $i++) {
        try {
            $gr = Invoke-RestMethod -Uri 'https://mld-yu.github.io/JuniGrid/game-versions.json' -TimeoutSec 10
            if ($gr.updated_at -eq $localDoc.updated_at -and @($gr.versions).Count -eq @($localDoc.versions).Count) {
                $served = $true
                break
            }
        } catch { }
        Start-Sleep -Seconds 3
    }
    if ($served) { Write-Host "线上已生效：$($localDoc.updated_at)，$(@($localDoc.versions).Count) 个版本" }
    else { Write-Host "⚠ 线上还没生效（Pages 可能仍在构建 / CDN 缓存）—— 过一两分钟自己访问上面的地址确认" -ForegroundColor Yellow }
}

Write-Host ""
Write-Host "提示：内嵌副本（编进程序集的那份）只在发新版时才更新；不发版时它在老客户端里会一直是旧的，"
Write-Host "      但客户端会优先用验签通过的在线表，所以不影响「加新版本」这件事。"
