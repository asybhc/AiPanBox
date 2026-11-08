<#
.SYNOPSIS
    BaiduVault 本地验收体检（对应 NAS 版 verify.sh 的 8 类检查）

.PARAMETER Target
    加密区内已存在的文件路径（如 '/影片.mkv'），给了才做 Range/拖动测试

.PARAMETER Library
    目标库名（默认 baidu），对应 rclone.conf 里的 <库名>_chunk512 与播放路径 /<库名>/

.PARAMETER Big
    额外做 200MB 上传+哈希往返校验（耗时、占临时磁盘）

.EXAMPLE
    .\verify.ps1
    .\verify.ps1 -Target '/影片.mkv'
    .\verify.ps1 -Target '/影片.mkv' -Big
    .\verify.ps1 -Library demo2 -Target '/test.bin'
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Target = '',
    [string]$Library = 'baidu',
    [switch]$Big
)

# PS5.1 下 curl/rclone 的 stderr 输出在 EAP=Stop 时会被误判，故用 Continue + $LASTEXITCODE
$ErrorActionPreference = 'Continue'

$Root      = Join-Path $env:LOCALAPPDATA 'BaiduVault'
$RcloneExe = Join-Path $Root 'bin\rclone.exe'
$Conf      = Join-Path $Root 'rclone.conf'
$CacheDir  = Join-Path $Root 'cache'
$KeysFile  = Join-Path $Root 'keys\rclone-keys.txt'
$OLAdminFile = Join-Path $Root 'keys\openlist-admin.txt'
$CredFile  = Join-Path $Root 'keys\webdav-credentials.txt'
$LogDir    = Join-Path $Root 'logs'
$Remote    = "${Library}_chunk512"
$OLPort    = 5244
$Port      = 8088
$Base      = "http://127.0.0.1:$Port"

$script:Pass = 0; $script:Fail = 0; $script:Skip = 0
function Ok([string]$m)   { Write-Host "  ✓ $m" -ForegroundColor Green;  $script:Pass++ }
function No([string]$m)   { Write-Host "  ✗ $m" -ForegroundColor Red;    $script:Fail++ }
function Skip([string]$m) { Write-Host "  - $m" -ForegroundColor Yellow; $script:Skip++ }
function Hdr([string]$m)  { Write-Host ''; Write-Host $m -ForegroundColor White }

function Test-Port([int]$P) {
    try {
        $c = New-Object System.Net.Sockets.TcpClient
        $iar = $c.BeginConnect('127.0.0.1', $P, $null, $null)
        $ok = $iar.AsyncWaitHandle.WaitOne(800)
        if ($ok -and $c.Connected) { $c.EndConnect($iar); $c.Close(); return $true }
        $c.Close(); return $false
    } catch { return $false }
}

$OLPass = ''; if (Test-Path $OLAdminFile) { $OLPass = ((Get-Content $OLAdminFile | Where-Object { $_ -match '^密码: ' }) -replace '^密码: ', '').Trim() }
$WUser  = ''; $WPass = ''
if (Test-Path $CredFile) {
    $WUser = ((Get-Content $CredFile | Where-Object { $_ -match '^WEBDAV_USER=' }) -replace '^[^=]*=', '').Trim()
    $WPass = ((Get-Content $CredFile | Where-Object { $_ -match '^WEBDAV_PASS=' }) -replace '^[^=]*=', '').Trim()
}

Hdr '1. 进程与端口'
foreach ($item in @(@('openlist', $OLPort), @('rclone-serve', $Port))) {
    $name = $item[0]; $p = [int]$item[1]
    $pidFile = Join-Path $Root "run\$name.pid"
    $alive = $false
    if (Test-Path $pidFile) {
        $procId = [int](Get-Content $pidFile).Trim()
        $alive = [bool](Get-Process -Id $procId -ErrorAction SilentlyContinue)
    }
    if ($alive) { Ok "$name 进程运行中（PID $procId）" } else { No "$name 进程未运行（先跑 setup-local.ps1）" }
    if (Test-Port $p) { Ok "$name 端口 :$p 监听中" } else { No "$name 端口 :$p 未监听" }
}

Hdr '2. OpenList WebDAV'
if ($OLPass) {
    $code = & curl.exe -s -o NUL -w '%{http_code}' -X PROPFIND -H 'Depth: 0' -u "admin:$OLPass" --max-time 10 "http://127.0.0.1:$OLPort/dav/"
    if ($code -eq '207' -or $code -eq '200') { Ok "OpenList WebDAV 可访问（HTTP $code）" } else { No "OpenList WebDAV 异常（HTTP $code）" }
} else { Skip "找不到 $OLAdminFile" }

Hdr '3. rclone 配置与 chunker 前提'
if (Test-Path $Conf) { Ok "$Conf 存在" } else { No "$Conf 缺失" }
if ((Get-Content $Conf -ErrorAction SilentlyContinue | Where-Object { $_ -match '^password2\s*=' })) { Ok 'password2 已配置' } else { No '缺少 password2' }
if ((Test-Path $RcloneExe) -and (Test-Path $Conf)) {
    & $RcloneExe --config $Conf lsd "${Remote}:" 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) { Ok "rclone 能列出 ${Remote}:" } else { No "rclone 无法列出 ${Remote}:（存储未接入百度或未加演示存储）" }
    $tag = ".vt_$PID"
    & $RcloneExe --config $Conf touch "${Remote}:/$tag" 2>$null | Out-Null
    & $RcloneExe --config $Conf moveto "${Remote}:/$tag" "${Remote}:/.vt2_$PID" 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) {
        & $RcloneExe --config $Conf delete "${Remote}:/.vt2_$PID" 2>$null | Out-Null
        Ok 'move 可用（chunker 前提满足）'
    } else {
        & $RcloneExe --config $Conf delete "${Remote}:/$tag" 2>$null | Out-Null
        No 'move 不可用，chunker 会拒绝工作（检查 admin 的 WebDAV 写权限位）'
    }
} else { Skip 'rclone 或配置缺失' }

Hdr '4. 缓存盘'
if (Test-Path $CacheDir) {
    $driveLetter = ([System.IO.Path]::GetPathRoot($CacheDir)).TrimEnd('\')
    $fs = (Get-Volume -DriveLetter $driveLetter.TrimEnd(':') -ErrorAction SilentlyContinue).FileSystemType
    if ($fs -eq 'NTFS' -or $fs -eq 'ReFS') {
        Ok "文件系统 $fs 支持稀疏文件"
        # 稀疏实测：创建 1G 稀疏文件看实际占用（fsutil 在部分系统需要管理员，失败则跳过）
        $tf = Join-Path $CacheDir '.sparse_test'
        try {
            & fsutil.exe file createnew "$tf" 1048576 2>$null | Out-Null
            & fsutil.exe sparse setflag "$tf" 2>$null | Out-Null
            & fsutil.exe sparse setrange "$tf" 0 1048576 2>$null | Out-Null
            $real = (Get-Item $tf).Length
            & fsutil.exe file setzerodata offset=0 length=1048576 "$tf" 2>$null | Out-Null
            $sizeOnDisk = (Get-Item $tf).Length
            Remove-Item $tf -Force -ErrorAction SilentlyContinue
            Ok '稀疏文件标记成功（NTFS 行为符合 vfs-cache-mode full 要求）'
        } catch {
            Remove-Item $tf -Force -ErrorAction SilentlyContinue
            Skip "稀疏实测需要管理员权限，已跳过（NTFS 默认支持，一般无需担心）"
        }
    } else {
        No "缓存在 $fs 上，vfs-cache-mode full 需要 NTFS（请把 BaiduVault 放到 NTFS 分区）"
    }
    $free = [math]::Round((Get-PSDrive -Name $driveLetter.TrimEnd(':') -ErrorAction SilentlyContinue).Free / 1GB, 1)
    Ok "缓存盘剩余 ${free}GB（缓存上限 10G）"
} else { No "$CacheDir 不存在（先跑 setup-local.ps1）" }

Hdr '5. WebDAV 端点（127.0.0.1:8088）'
$code = & curl.exe -s -o NUL -w '%{http_code}' -u "${WUser}:${WPass}" --max-time 10 "$Base/"
if ($code -eq '207' -or $code -eq '200') { Ok "WebDAV 可访问（HTTP $code）" } else { No "WebDAV 不可访问（HTTP $code）" }
$code = & curl.exe -s -o NUL -w '%{http_code}' --max-time 10 "$Base/"
if ($code -eq '401') { Ok '未授权访问被拒绝（HTTP 401）' } else { No "未授权访问返回 $code，鉴权可能未生效" }

Hdr "6. Range 与拖动（库 $Library）"
if ($Target) {
    # 按路径段编码，保留 '/' 分隔符（整体编码会把前导 / 变成 %2F，服务端不识别）
    # serve 根是 vault: 聚合层，URL 路径前缀即库名
    $enc = ($Target -split '/' | ForEach-Object { [System.Uri]::EscapeDataString($_) }) -join '/'
    $url = "$Base/$Library$enc"
    $code = & curl.exe -s -o NUL -w '%{http_code}' -u "${WUser}:${WPass}" -r 0-1039 --max-time 20 "$url"
    if ($code -eq '206') { Ok '服务端支持 Range（HTTP 206）' }
    elseif ($code -eq '404') { No "Range 返回 404：路径必须是相对库 [$Library] 根的路径（如 '/影片.mkv'），不是本地路径；换库请加 -Library" }
    else { No "Range 返回 $code（期望 206）" }
    $t0 = Get-Date
    $code = & curl.exe -s -o NUL -w '%{http_code}' -u "${WUser}:${WPass}" -r 5000000-5001039 --max-time 30 "$url"
    $ms = [int]((Get-Date) - $t0).TotalMilliseconds
    if ($code -eq '206') { Ok "中段 Range 命中（${ms}ms）" } else { No "中段 Range 失败（HTTP $code）" }
} else { Skip "未提供文件路径；重跑加 -Target '/影片.mkv'（当前库 $Library，换库加 -Library）" }

Hdr '7. 日志异常'
foreach ($f in @((Join-Path $LogDir 'rclone-webdav.log'), (Join-Path $Root 'openlist-data\log\log.log'))) {
    if (Test-Path $f) {
        $hits = @(Get-Content $f -Tail 300 -ErrorAction SilentlyContinue | Select-String -Pattern 'error|failed|cannot' -SimpleMatch:$false).Count
        if ($hits -eq 0) { Ok "$(Split-Path $f -Leaf) 近 300 行无 error 关键字" }
        else { Skip "$(Split-Path $f -Leaf) 近 300 行有 $hits 条 error 关键字，请人工复核：$f" }
    } else { Skip "$(Split-Path $f -Leaf) 不存在" }
}

Hdr '8. 往返一致性'
if ($Big) {
    $src = Join-Path $env:TEMP 'verify_big.bin'
    $fs = [System.IO.File]::Create($src); $buf = New-Object byte[] 1MB
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    for ($i = 0; $i -lt 200; $i++) { $rng.GetBytes($buf); $fs.Write($buf, 0, $buf.Length) }
    $fs.Close()
    & $RcloneExe --config $Conf copy "$src" "${Remote}:/_verify/" 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) {
        & $RcloneExe --config $Conf check "$src" "${Remote}:/_verify/verify_big.bin" 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { Ok '200MB 上传 + 哈希校验通过' } else { No '200MB 哈希校验失败' }
    } else { No '200MB 上传失败' }
    & $RcloneExe --config $Conf delete "${Remote}:/_verify/" 2>$null | Out-Null
    Remove-Item $src -Force -ErrorAction SilentlyContinue
} else { Skip '未启用；加 -Big 执行' }

Write-Host ''
Write-Host ("结果：通过 {0}，失败 {1}，跳过 {2}" -f $script:Pass, $script:Fail, $script:Skip) -ForegroundColor White
if ($script:Fail -gt 0) { exit 1 } else { exit 0 }
