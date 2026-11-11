#Requires -Version 7.0
<#
.SYNOPSIS
    在两台电脑之间同步 JuniGrid 的「mod 自定义分类」（只搬分类，不搬 mod 文件）。

.DESCRIPTION
    分类只存在 %APPDATA%\JuniGrid\junigrid.config.json 的三段里：
        modUserCategory        mod 顶层文件夹名 → 分类名（多选时 "a,b" 逗号拼接）
        modUserCategoryOrder   分类展示顺序
        modCategoryIcon        分类图标

    本脚本只搬这三段，其余字段（gamePath、N 网 token、Nexus 安装记录、
    overlays / contentOverrides、封面缓存、存档 profile…）一律不碰 ——
    整份覆盖配置会把对方电脑的对机设置冲掉。

    因为键是「mod 顶层文件夹名」，两台电脑的 mod 目录结构一致时分类才能对上。
    文件夹被改过名的那几条会落空（脚本会把它们列出来）。

.PARAMETER Package
    导出包（一个几十 KB 的 JSON）存放/读取的路径 —— 就是个搬运介质，
    存桌面、U 盘、网盘目录都行，与 JuniGrid 的数据位置无关（真实数据仍在
    %APPDATA%\JuniGrid\junigrid.config.json，脚本只搬其中三段）。
    Export 不写则默认存到桌面；Import 必须指定。

.EXAMPLE
    # 电脑 A（已经分好类）：导出到桌面（默认位置，可以省略第二个参数）
    .\sync-mod-categories.ps1 Export

.EXAMPLE
    # 指定导出包位置：U 盘 / 网盘目录都行
    .\sync-mod-categories.ps1 Export E:\junigrid-categories.json

.EXAMPLE
    # 把文件拷到电脑 B：导入（与 B 上已有分类合并，同一 mod / 同名分类以导出的为准）
    .\sync-mod-categories.ps1 Import "$env:USERPROFILE\Desktop\junigrid-categories.json"

.EXAMPLE
    # 想把 B 上的分类整个换掉
    .\sync-mod-categories.ps1 Import "$env:USERPROFILE\Desktop\junigrid-categories.json" -Replace

.NOTES
    运行前请先退出 JuniGrid：程序退出时会把内存里的配置写回盘，覆盖掉刚导入的内容。
#>
param(
    [Parameter(Mandatory, Position = 0)]
    [ValidateSet('Export', 'Import')]
    [string]$Mode,

    # 导出包的路径。Export 省略 = 桌面；Import 必填
    [Parameter(Position = 1)]
    [string]$Package,

    # 指定配置文件（默认 %APPDATA%\JuniGrid\junigrid.config.json），主要给测试用
    [string]$Config,
    [string]$ConfigDir,

    # Import 时用包里的分类整段替换，而不是合并
    [switch]$Replace
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$CategoryKeys = @('modUserCategory', 'modUserCategoryOrder', 'modCategoryIcon')
# 配置文件字段名 → 导出包字段名（与程序内 ModCategoryShareService 的格式保持一致，
# 这样脚本导出的包能在界面里导入，界面导出的包也能用脚本导入）
$PkgFields = @{
    'modUserCategory'      = 'categories'
    'modUserCategoryOrder' = 'order'
    'modCategoryIcon'      = 'icons'
}
$JsonOpts = [System.Text.Json.JsonSerializerOptions]::new()
$JsonOpts.WriteIndented = $true
$JsonOpts.Encoder = [System.Text.Encodings.Web.JavaScriptEncoder]::UnsafeRelaxedJsonEscaping
$Utf8NoBom = [System.Text.UTF8Encoding]::new($false)
$JObj = [System.Text.Json.Nodes.JsonObject]
$JArr = [System.Text.Json.Nodes.JsonArray]
$JVal = [System.Text.Json.Nodes.JsonValue]

function Get-JsonConfigPath {
    if ($Config) { return [System.IO.Path]::GetFullPath($Config) }
    $dir = if ($ConfigDir) { $ConfigDir } else { Join-Path $env:APPDATA 'JuniGrid' }
    return [System.IO.Path]::Combine($dir, 'junigrid.config.json')
}

function Read-JsonFile([string]$path) {
    $node = [System.Text.Json.Nodes.JsonNode]::Parse([System.IO.File]::ReadAllText($path))
    if ($node -isnot $JObj) { throw "不是 JSON 对象：$path" }
    return ,($node)   # 逗号：别让 PS 把 JsonObject 当集合拆开
}

function Save-JsonFile([string]$path, $node) {
    [System.IO.File]::WriteAllText($path, $node.ToJsonString($JsonOpts), $Utf8NoBom)
}

# 老配置可能是 PascalCase —— 取键一律不区分大小写，返回文件里真实的键名
function Find-Key($obj, [string]$name) {
    foreach ($p in $obj) {
        if ([string]::Equals($p.Key, $name, [System.StringComparison]::OrdinalIgnoreCase)) { return [string]$p.Key }
    }
    return $null
}

# 读属性：遍历取 Value，不走索引器（索引器在 PS 里会被误选成 int 重载）。
# 必须用 `,` 包一层 —— JsonObject 是可枚举的，直接 return 会被 PS 拆成 KeyValuePair 序列。
function Get-Prop($obj, [string]$name) {
    foreach ($p in $obj) {
        if ([string]::Equals($p.Key, $name, [System.StringComparison]::OrdinalIgnoreCase)) { return ,($p.Value) }
    }
    return $null
}

function Set-Prop($obj, [string]$name, $node) {
    $real = Find-Key $obj $name
    if ($real) { [void]$obj.Remove($real) }
    $obj.Add([string]$name, $node)
}

# 按候选名依次取（导入时同时兼容程序内格式 categories/order/icons 与旧脚本格式 modUserCategory/…）
function Get-PropAny($obj, [string[]]$names) {
    foreach ($n in $names) {
        $v = Get-Prop $obj $n
        if ($null -ne $v) { return ,($v) }
    }
    return $null
}

function Get-Or-Add-Object($obj, [string]$name) {
    $v = Get-Prop $obj $name
    if ($v -is $JObj) { return ,($v) }
    Set-Prop $obj $name ($JObj::new())
    return ,(Get-Prop $obj $name)
}

function Get-Or-Add-Array($obj, [string]$name) {
    $v = Get-Prop $obj $name
    if ($v -is $JArr) { return ,($v) }
    Set-Prop $obj $name ($JArr::new())
    return ,(Get-Prop $obj $name)
}

function Get-NodeString($node) {    if ($null -eq $node) { return $null }
    if ($node -is $JVal -and $node.GetValueKind() -eq [System.Text.Json.JsonValueKind]::String) {
        return $node.GetValue[string]()
    }
    return $node.ToJsonString()
}

function New-Str([string]$s) { return $JVal::Create([string]$s) }

# 分类键归一：去掉禁用前缀的点、只取第一段（与 Mods.razor 的 CategoryKeyOf 同口径）
function Get-NormalKey([string]$key) {
    $k = $key.TrimStart('.')
    $i = $k.IndexOf('/')
    if ($i -ge 0) { $k = $k.Substring(0, $i) }
    return $k
}

if (-not $Package) {
    if ($Mode -ne 'Export') {
        throw 'Import 需要指定导出包路径，例如： .\sync-mod-categories.ps1 Import "$env:USERPROFILE\Desktop\junigrid-categories.json"'
    }
    # 只影响「导出包放哪」，不影响 JuniGrid 自己的数据位置
    $Package = Join-Path ([Environment]::GetFolderPath('Desktop')) 'junigrid-categories.json'
}

$configPath = Get-JsonConfigPath
if (-not (Test-Path -LiteralPath $configPath)) {
    throw "找不到配置文件：$configPath`n请先在目标电脑上启动一次 JuniGrid、设好游戏目录，退出后再运行本脚本。"
}

if ($Mode -eq 'Export') {
    $cfg = Read-JsonFile $configPath
    $pkg = $JObj::new()
    # 字段名与程序内「导出分类方案」一致（ModCategoryShareService），两边导出的包可以互读
    Set-Prop $pkg 'schema' (New-Str 'junigrid-categories/1')
    Set-Prop $pkg 'exportedAt' (New-Str (Get-Date).ToString('o'))
    Set-Prop $pkg 'appVersion' (New-Str 'script')

    $catCount = 0
    $nameCount = 0
    foreach ($k in $CategoryKeys) {
        $node = Get-Prop $cfg $k
        if ($null -eq $node) { Set-Prop $pkg $PkgFields[$k] $null; continue }
        Set-Prop $pkg $PkgFields[$k] $node.DeepClone()
        if ($k -eq 'modUserCategory' -and $node -is $JObj) { $catCount = $node.Count }
        if ($k -eq 'modUserCategoryOrder' -and $node -is $JArr) { $nameCount = $node.Count }
    }

    $pkgPath = [System.IO.Path]::GetFullPath($Package)
    Save-JsonFile $pkgPath $pkg
    Write-Host "已导出：$pkgPath" -ForegroundColor Green
    Write-Host ("  mod 归类记录 {0} 条，分类顺序 {1} 项" -f $catCount, $nameCount)
    Write-Host '把这个文件拷到另一台电脑，运行： .\sync-mod-categories.ps1 Import <文件>' -ForegroundColor DarkGray
    return
}

# ---------------- Import ----------------
$pkgPath = [System.IO.Path]::GetFullPath($Package)
if (-not (Test-Path -LiteralPath $pkgPath)) { throw "找不到导出包：$pkgPath" }
$pkg = Read-JsonFile $pkgPath

$backup = $configPath + '.syncbak-' + (Get-Date).ToString('yyyyMMdd-HHmmss')
Copy-Item -LiteralPath $configPath -Destination $backup -Force
Write-Host "已备份原配置：$backup" -ForegroundColor DarkGray

$cfg = Read-JsonFile $configPath

# 1) mod → 分类
$tgtCats = Get-Or-Add-Object $cfg 'modUserCategory'
if ($Replace) { $tgtCats.Clear() }
$pkgCats = Get-PropAny $pkg @('categories', 'modUserCategory')

$added = 0; $updated = 0
if ($pkgCats -is $JObj) {
    foreach ($p in $pkgCats) {
        $value = Get-NodeString $p.Value
        if ([string]::IsNullOrWhiteSpace($value)) { continue }
        # 目标里已有「同一 mod」的键（忽略大小写、忽略禁用点前缀）→ 就地更新，别留两条
        $norm = Get-NormalKey ([string]$p.Key)
        $existing = $null
        foreach ($t in $tgtCats) {
            if ([string]::Equals((Get-NormalKey ([string]$t.Key)), $norm, [System.StringComparison]::OrdinalIgnoreCase)) {
                $existing = [string]$t.Key; break
            }
        }
        if ($existing) {
            if ((Get-NodeString (Get-Prop $tgtCats $existing)) -ne $value) { $updated++ }
            Set-Prop $tgtCats $existing (New-Str $value)
        }
        else {
            Set-Prop $tgtCats ([string]$p.Key) (New-Str $value)
            $added++
        }
    }
}

# 2) 分类顺序（追加缺失项，保留目标已有的先后）
$tgtOrder = Get-Or-Add-Array $cfg 'modUserCategoryOrder'
if ($Replace) { $tgtOrder.Clear() }
$pkgOrder = Get-PropAny $pkg @('order', 'modUserCategoryOrder')
$orderAdded = 0
if ($pkgOrder -is $JArr) {
    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($o in $tgtOrder) { [void]$seen.Add([string](Get-NodeString $o)) }
    foreach ($o in $pkgOrder) {
        $name = Get-NodeString $o
        # 空串在 order 里是「未分类」那一段的占位，照搬（界面位置才和源机一致）
        if ($null -eq $name) { continue }
        if ($seen.Add($name)) { $tgtOrder.Add((New-Str $name)); $orderAdded++ }
    }
}

# 3) 分类图标
$tgtIcon = Get-Or-Add-Object $cfg 'modCategoryIcon'
if ($Replace) { $tgtIcon.Clear() }
$pkgIcon = Get-PropAny $pkg @('icons', 'modCategoryIcon')
$iconSet = 0
if ($pkgIcon -is $JObj) {
    foreach ($p in $pkgIcon) {
        Set-Prop $tgtIcon ([string]$p.Key) (New-Str ([string](Get-NodeString $p.Value)))
        $iconSet++
    }
}

Save-JsonFile $configPath $cfg

Write-Host "已写入：$configPath" -ForegroundColor Green
Write-Host ("  新增归类 {0} 条，更新 {1} 条；分类顺序补充 {2} 项；图标 {3} 个" -f $added, $updated, $orderAdded, $iconSet)

# 4) 体检：这些文件夹在本机 Mods 里找不到 → 该条不会生效
$gamePath = Get-NodeString (Get-Prop $cfg 'gamePath')
if ([string]::IsNullOrWhiteSpace($gamePath)) {
    Write-Host '配置里没有游戏路径（gamePath），跳过文件夹核对。' -ForegroundColor Yellow
    return
}
$modsDir = Join-Path $gamePath 'Mods'
if (-not (Test-Path -LiteralPath $modsDir)) {
    Write-Host "找不到 mod 目录：$modsDir（跳过核对）" -ForegroundColor Yellow
    return
}
$missing = [System.Collections.Generic.List[string]]::new()
foreach ($p in (Get-Or-Add-Object $cfg 'modUserCategory')) {
    $n = Get-NormalKey ([string]$p.Key)
    if ($n.Length -eq 0) { continue }
    if (-not (Test-Path -LiteralPath (Join-Path $modsDir $n))) { $missing.Add([string]$p.Key) }
}
if ($missing.Count -eq 0) {
    Write-Host '全部分类记录都能在本机 Mods 里找到对应文件夹。' -ForegroundColor Green
}
else {
    Write-Host ("有 {0} 条分类记录在本机 Mods 里找不到同名文件夹（那几条会显示为未分类）：" -f $missing.Count) -ForegroundColor Yellow
    $missing | Select-Object -First 20 | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkYellow }
    if ($missing.Count -gt 20) { Write-Host ("  …另 {0} 条" -f ($missing.Count - 20)) -ForegroundColor DarkYellow }
}
Write-Host '现在可以启动 JuniGrid 了。' -ForegroundColor DarkGray
