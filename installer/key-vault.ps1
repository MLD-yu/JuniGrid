<#
.SYNOPSIS
    私钥保险箱：把签名私钥加密成一个文件（适合放 U 盘 / 移动硬盘 / 云盘）。

.DESCRIPTION
    为什么需要它：明文 .pem 拷到 U 盘，等于"谁捡到盘谁就能签你的更新"——
    那比丢了更糟。这个脚本用密码把私钥包成一个二进制文件：

        JGV1 | salt(16) | iv(16) | hmac(32) | 密文

    · 加密：AES-256-CBC（PKCS7 填充），Encrypt-then-MAC
    · 认证：HMAC-SHA256 覆盖 magic+salt+iv+密文（密码错/文件被改都会验不过）
    · 派生：PBKDF2-SHA256，300,000 次

    刻意只用 .NET Framework 4.7.2 就有的原语（Aes / HMACSHA256 / Rfc2898DeriveBytes），
    所以 PowerShell 5.1 和 7 都能跑，**换台电脑、换 Windows 版本都能解**——
    这点比 BitLocker 强：BitLocker 的 U 盘在 Windows 家庭版上打不开。

    密码不落在任何文件里，也不进命令历史（交互输入）。

.PARAMETER Encrypt
    要加密的私钥（.pem）。

.PARAMETER Decrypt
    要解密 .bak 文件。

.PARAMETER Out
    输出文件。加密时默认 <私钥名>.bak；解密时默认 <私钥名>.restored.pem。

.EXAMPLE
    .\installer\key-vault.ps1 -Encrypt "$env:USERPROFILE\.junigrid\keys\junigrid-manifest-2026-01.pem" -Out "E:\junigrid-key.bak"

.EXAMPLE
    .\installer\key-vault.ps1 -Decrypt "E:\junigrid-key.bak" -Out "$env:TEMP\restored.pem"
    .\installer\verify-key-backup.ps1 -KeyPath "$env:TEMP\restored.pem"
#>
[CmdletBinding(DefaultParameterSetName = 'Encrypt')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Encrypt')][string] $Encrypt,
    [Parameter(Mandatory = $true, ParameterSetName = 'Decrypt')][string] $Decrypt,
    [string] $Out,
    # ⚠ 仅供自动化/演练：手动跑请留空，走交互掩码输入。
    #   写在命令行上会进 PowerShell 历史（Get-History / PSReadLine 的 history 文件）。
    [string] $Password
)

$ErrorActionPreference = 'Stop'
$Magic = [Text.Encoding]::ASCII.GetBytes('JGV1')
$Iterations = 300000

function Read-Secret([string]$prompt) {
    try {
        # 正常交互：掩码输入，不回显
        $s = Read-Host -AsSecureString $prompt
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR(
            [Runtime.InteropServices.Marshal]::SecureStringToBSTR($s))
    }
    catch {
        # 非交互（管道喂入 / 自动化）：退回读标准输入。
        # 这条路密码会回显、也可能进调用方的历史，只适合演练与自动化。
        $line = [Console]::In.ReadLine()
        if ($null -eq $line) { throw "读不到密码（既不是交互式，也没有管道输入）" }
        return $line
    }
}

function Get-Password([string]$confirm) {
    if (-not [string]::IsNullOrEmpty($Password)) { return $Password }   # 自动化路径
    $p1 = Read-Secret "输入密码（至少 12 位，别用生日/手机号）"
    if ([string]::IsNullOrEmpty($p1)) { throw "密码不能为空" }
    if ($p1.Length -lt 12) { throw "密码太短（至少 12 位）—— 这是唯一保护私钥的东西" }
    if ($confirm) {
        $p2 = Read-Secret "再输一次确认"
        if ($p1 -ne $p2) { throw "两次输入不一致" }
    }
    return $p1
}

function New-Key([string]$password, [byte[]]$salt) {
    $kdf = New-Object System.Security.Cryptography.Rfc2898DeriveBytes(
        $password, $salt, $Iterations, [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    try { return , $kdf.GetBytes(64) } finally { $kdf.Dispose() }   # 前 32 = 加密密钥，后 32 = MAC 密钥
}

if ($Encrypt) {
    if (-not (Test-Path $Encrypt)) { throw "找不到私钥：$Encrypt" }
    if (-not $Out) { $Out = [IO.Path]::ChangeExtension($Encrypt, '.bak') }
    $plain = [IO.File]::ReadAllBytes((Resolve-Path $Encrypt).Path)
    if ($plain.Length -eq 0) { throw "私钥文件是空的" }

    $pw = Get-Password $true
    $salt = New-Object byte[] 16; $iv = New-Object byte[] 16
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $rng.GetBytes($salt); $rng.GetBytes($iv); $rng.Dispose()

    $keys = New-Key $pw $salt
    $aes = [System.Security.Cryptography.Aes]::Create()
    $aes.KeySize = 256; $aes.Mode = 'CBC'; $aes.Padding = 'PKCS7'; $aes.Key = $keys[0..31]; $aes.IV = $iv
    $cipher = $aes.CreateEncryptor().TransformFinalBlock($plain, 0, $plain.Length)
    $aes.Dispose()

    $body = New-Object byte[] ($Magic.Length + $salt.Length + $iv.Length + $cipher.Length)
    [Array]::Copy($Magic, 0, $body, 0, $Magic.Length)
    [Array]::Copy($salt, 0, $body, $Magic.Length, 16)
    [Array]::Copy($iv, 0, $body, $Magic.Length + 16, 16)
    [Array]::Copy($cipher, 0, $body, $Magic.Length + 32, $cipher.Length)

    $hmac = New-Object System.Security.Cryptography.HMACSHA256(, $keys[32..63])
    $mac = $hmac.ComputeHash($body)
    $hmac.Dispose()

    $blob = New-Object byte[] ($body.Length + 32)
    [Array]::Copy($body, 0, $blob, 0, $body.Length)
    [Array]::Copy($mac, 0, $blob, $body.Length, 32)
    [IO.File]::WriteAllBytes($Out, $blob)

    $sha = (Get-FileHash $Out -Algorithm SHA256).Hash.ToLower()
    Write-Host ""
    Write-Host "已加密：$Out（$($blob.Length) 字节）" -ForegroundColor Green
    Write-Host "SHA-256：$sha"
    Write-Host ""
    Write-Host "下一步：把 .bak 拷到 U 盘（这块盘现在可以随便丢，没有密码打不开）。"
    Write-Host "⚠ 密码本身要另外记住（密码管理器 / 脑子里）—— 忘了就永久解不开，没有任何后门。"
    Write-Host "⚠ 加密完请做一次解密演练：-Decrypt 解出来 → verify-key-backup.ps1 验一遍。"
}
else {
    if (-not (Test-Path $Decrypt)) { throw "找不到文件：$Decrypt" }
    if (-not $Out) { $Out = Join-Path (Split-Path -Parent $Decrypt) 'restored.pem' }
    $blob = [IO.File]::ReadAllBytes((Resolve-Path $Decrypt).Path)
    if ($blob.Length -lt 4 + 16 + 16 + 32 + 16) { throw "文件太小，不像本工具产出的 .bak" }
    for ($i = 0; $i -lt 4; $i++) { if ($blob[$i] -ne $Magic[$i]) { throw "不是本工具的 .bak（头部不对）" } }

    $bodyLen = $blob.Length - 32
    $body = New-Object byte[] $bodyLen
    [Array]::Copy($blob, 0, $body, 0, $bodyLen)
    $mac = New-Object byte[] 32
    [Array]::Copy($blob, $bodyLen, $mac, 0, 32)

    $salt = New-Object byte[] 16
    [Array]::Copy($body, 4, $salt, 0, 16)
    $pw = Get-Password $false
    $keys = New-Key $pw $salt

    $hmac = New-Object System.Security.Cryptography.HMACSHA256(, $keys[32..63])
    $calc = $hmac.ComputeHash($body)
    $hmac.Dispose()
    $diff = 0
    for ($i = 0; $i -lt 32; $i++) { $diff = $diff -bor ($calc[$i] -bxor $mac[$i]) }
    if ($diff -ne 0) { throw "密码不对，或文件被改过（认证失败）" }

    $iv = New-Object byte[] 16
    [Array]::Copy($body, 20, $iv, 0, 16)
    $cipherLen = $bodyLen - 36
    $cipher = New-Object byte[] $cipherLen
    [Array]::Copy($body, 36, $cipher, 0, $cipherLen)

    $aes = [System.Security.Cryptography.Aes]::Create()
    $aes.KeySize = 256; $aes.Mode = 'CBC'; $aes.Padding = 'PKCS7'; $aes.Key = $keys[0..31]; $aes.IV = $iv
    try { $plain = $aes.CreateDecryptor().TransformFinalBlock($cipher, 0, $cipher.Length) }
    catch { throw "解密失败（密码错？文件截断？）" }
    finally { $aes.Dispose() }

    [IO.File]::WriteAllBytes($Out, $plain)
    Write-Host ""
    Write-Host "已解密：$Out（$($plain.Length) 字节）" -ForegroundColor Green
    Write-Host "请立刻验一遍能不能用："
    Write-Host "  .\installer\verify-key-backup.ps1 -KeyPath `"$Out`""
}
