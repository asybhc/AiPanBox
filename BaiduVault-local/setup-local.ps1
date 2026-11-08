<#
.SYNOPSIS
    BaiduVault 本地一键部署（Windows 桌面版，绿色免安装）

.DESCRIPTION
    架构：网盘(/apps/{应用名}) → OpenList(WebDAV :5244) → 每库 rclone crypt → chunker 512M
          → [vault] combine 聚合 → rclone serve webdav (127.0.0.1:8088，单进程单端口) → 播放器
    多库：OpenList 里每个挂载路径（/baidu、/ali……）自动成为一个库，URL 路径前缀即库名。
    全部数据/配置/缓存放在 %LOCALAPPDATA%\BaiduVault\，不装服务、不写注册表。

.PARAMETER BaiduClientId
    百度开发者应用 AppKey（可选；给了三个百度参数就自动添加百度网盘存储）

.PARAMETER BaiduClientSecret
    百度开发者应用 SecretKey

.PARAMETER BaiduRefreshToken
    百度授权码模式取得的 refresh_token（只能用一次，务必原样保存）

.PARAMETER BaiduRootFolder
    百度网盘根文件夹，默认 /apps（建议填 /apps/{你的应用名}）

.PARAMETER DemoLocal
    没有百度参数时，添加一个指向 %LOCALAPPDATA%\BaiduVault\local-store 的本地演示存储，
    用于先把整条加密链路跑通（之后可在 OpenList 后台删掉换成百度网盘）

.PARAMETER DemoLibraries
    额外添加若干本地目录演示库（如 -DemoLibraries demo2,demo3），各挂到 /<名字>，
    底层目录 %LOCALAPPDATA%\BaiduVault\local-store-<名字>，各自独立加密密钥

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\setup-local.ps1
    powershell -ExecutionPolicy Bypass -File .\setup-local.ps1 -BaiduClientId xxx -BaiduClientSecret yyy -BaiduRefreshToken zzz -BaiduRootFolder "/apps/我的应用"
    powershell -ExecutionPolicy Bypass -File .\setup-local.ps1 -DemoLocal
    powershell -ExecutionPolicy Bypass -File .\setup-local.ps1 -DemoLibraries demo2,demo3

.NOTES
    幂等：可重复执行，已完成的步骤自动跳过。
#>
[CmdletBinding()]
param(
    [string]$BaiduClientId = '',
    [string]$BaiduClientSecret = '',
    [string]$BaiduRefreshToken = '',
    [string]$BaiduRootFolder = '/apps',
    [switch]$DemoLocal,
    [string[]]$DemoLibraries = @(),
    [int]$OpenListPort = 5244,
    [int]$WebDavPort = 8088
)

# 注意：不能用 'Stop'——PS5.1 下 curl/rclone 向 stderr 打进度会被当成终止错误。
# 原生命令统一靠 $LASTEXITCODE 判断；关键 cmdlet 显式 -ErrorAction Stop + try/catch。
$ErrorActionPreference = 'Continue'

# ================= 固定路径 =================
$Root       = Join-Path $env:LOCALAPPDATA 'BaiduVault'
$BinDir     = Join-Path $Root 'bin'
$RcloneExe  = Join-Path $BinDir 'rclone.exe'
$OpenListExe= Join-Path $BinDir 'openlist.exe'
$OLData     = Join-Path $Root 'openlist-data'
$CacheDir   = Join-Path $Root 'cache'
$KeysDir    = Join-Path $Root 'keys'
$KeysFile   = Join-Path $KeysDir 'rclone-keys.txt'
$OLAdminFile= Join-Path $KeysDir 'openlist-admin.txt'
$CredFile   = Join-Path $KeysDir 'webdav-credentials.txt'
$Conf       = Join-Path $Root 'rclone.conf'
$LogDir     = Join-Path $Root 'logs'
$RunDir     = Join-Path $Root 'run'
$LocalStore = Join-Path $Root 'local-store'
$RcAddr     = 'http://127.0.0.1:5572'

# ================= 工具函数 =================
function Log([string]$msg)  { Write-Host ("[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $msg) -ForegroundColor Green }
function Warn([string]$msg) { Write-Host ("[WARN] {0}" -f $msg) -ForegroundColor Yellow }
function Die([string]$msg)  { Write-Host ("[FAIL] {0}" -f $msg) -ForegroundColor Red; exit 1 }

function Test-Port([int]$Port) {
    try {
        $c = New-Object System.Net.Sockets.TcpClient
        $iar = $c.BeginConnect('127.0.0.1', $Port, $null, $null)
        $ok = $iar.AsyncWaitHandle.WaitOne(800)
        if ($ok -and $c.Connected) { $c.EndConnect($iar); $c.Close(); return $true }
        $c.Close(); return $false
    } catch { return $false }
}

function Wait-Port([int]$Port, [int]$Seconds, [string]$Name) {
    for ($i = 0; $i -lt $Seconds; $i++) {
        if (Test-Port $Port) { return }
        Start-Sleep -Seconds 1
    }
    Die "$Name 未在 $Port 端口监听（等了 ${Seconds}s）。请看日志：$LogDir"
}

function Start-Detached([string]$CmdLine, [string]$WorkDir) {
    # 用 WMI Win32_Process.Create 拉起脱离调用方进程树的后台进程（隐藏窗口）。
    # 等价于 Start-Process -WindowStyle Hidden，但不会随启动它的 shell 退出而被回收。
    # 注意：WMI 不继承调用方环境变量，所以不能靠它传 OPENLIST_* 变量（首初始化走 API）。
    $si = ([wmiclass]'Win32_ProcessStartup').CreateInstance()
    $si.ShowWindow = 0
    $r = ([wmiclass]'Win32_Process').Create($CmdLine, $WorkDir, $si)
    if ($r.ReturnValue -ne 0) { Die "启动失败（ReturnValue=$($r.ReturnValue)）：$CmdLine" }
    return $r.ProcessId
}

function New-RandomBase64([int]$Bytes) {
    $buf = New-Object byte[] $Bytes
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($buf)
    return [Convert]::ToBase64String($buf)
}

function Protect-File([string]$Path) {
    # 收紧权限：去掉继承，仅当前用户完全控制（尽力而为，失败仅警告）
    try {
        $user = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
        & icacls.exe "$Path" /inheritance:d 2>&1 | Out-Null
        & icacls.exe "$Path" /grant:r "${user}:(F)" 2>&1 | Out-Null
    } catch { Warn "收紧 $Path 权限失败（不影响功能）：$($_.Exception.Message)" }
}

function Download-File([string]$Url, [string]$OutFile, [string[]]$Mirrors) {
    # 直连 + 镜像前缀回退（镜像仅加速，之后仍做官方 sha256 校验）
    $targets = @($Url) + ($Mirrors | ForEach-Object { "$_$Url" })
    foreach ($t in $targets) {
        Log "下载：$t"
        & curl.exe -fL --retry 2 --connect-timeout 20 --max-time 900 -o "$OutFile" "$t" 2>$null
        if (($LASTEXITCODE -eq 0) -and (Test-Path $OutFile) -and ((Get-Item $OutFile).Length -gt 1MB)) { return $true }
    }
    return $false
}

Log "数据目录：$Root"

# ================= 1. 目录与基础检查 =================
foreach ($d in @($Root, $BinDir, $OLData, $CacheDir, $KeysDir, $LogDir, $RunDir, $LocalStore)) {
    if (-not (Test-Path $d)) { New-Item -ItemType Directory -Force $d | Out-Null }
}
if (-not (Get-Command curl.exe -ErrorAction SilentlyContinue)) {
    Die "找不到 curl.exe（Windows 10 1803+ 自带）。请升级系统或把 curl.exe 放进 PATH。"
}

# ================= 2. rclone.exe =================
if (Test-Path $RcloneExe) {
    Log "rclone 已存在：$((& $RcloneExe version 2>$null | Select-Object -First 1))"
} else {
    Log "下载 rclone（官方 downloads.rclone.org）"
    $zip = Join-Path $env:TEMP 'rclone-win.zip'
    if (-not (Download-File 'https://downloads.rclone.org/rclone-current-windows-amd64.zip' $zip @())) {
        Die "rclone 下载失败。请手动下载 https://downloads.rclone.org/rclone-current-windows-amd64.zip ，解压把 rclone.exe 放到 $BinDir 后重跑。"
    }
    $tmp = Join-Path $env:TEMP ("rclone-x-" + [Guid]::NewGuid().ToString('N'))
    Expand-Archive -Path $zip -DestinationPath $tmp -Force
    $exe = Get-ChildItem $tmp -Recurse -Filter rclone.exe | Select-Object -First 1
    if (-not $exe) { Die "rclone 压缩包里找不到 rclone.exe" }
    Copy-Item $exe.FullName $RcloneExe -Force
    Remove-Item -Recurse -Force $tmp, $zip -ErrorAction SilentlyContinue
    Log "rclone 就绪"
}

# ================= 3. openlist.exe =================
if (Test-Path $OpenListExe) {
    Log "OpenList 已存在，跳过下载"
} else {
    Log "查询 OpenList 最新 release"
    try {
        $rel = Invoke-RestMethod -Uri 'https://api.github.com/repos/OpenListTeam/OpenList/releases/latest' -Headers @{ 'User-Agent' = 'BaiduVault-setup' } -TimeoutSec 30 -ErrorAction Stop
    } catch {
        Die "无法访问 GitHub API。请手动下载 openlist-windows-amd64.zip（OpenListTeam/OpenList Releases），解压把 openlist.exe 放到 $BinDir 后重跑。"
    }
    $asset = $rel.assets | Where-Object { $_.name -eq 'openlist-windows-amd64.zip' } | Select-Object -First 1
    if (-not $asset) { Die "release 里没有 openlist-windows-amd64.zip，请手动下载：https://github.com/OpenListTeam/OpenList/releases" }
    Log "目标：$($asset.name)（$($rel.tag_name)）"
    $zip = Join-Path $env:TEMP 'openlist-win.zip'
    $mirrors = @('https://ghfast.top/', 'https://gh-proxy.com/', 'https://ghproxy.net/')
    if (-not (Download-File $asset.browser_download_url $zip $mirrors)) {
        Die "OpenList 所有下载通道均失败。请手动下载 $($asset.browser_download_url) ，解压把 openlist.exe 放到 $BinDir 后重跑。"
    }
    if ($asset.digest -and $asset.digest -like 'sha256:*') {
        $want = $asset.digest.Substring(7).ToLower()
        $got  = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
        if ($want -ne $got) { Remove-Item $zip -Force; Die "sha256 校验失败（官方 $want，实际 $got），文件可能被篡改，已中止。" }
        Log "sha256 校验通过"
    } else {
        Warn "release 未提供 sha256 摘要，跳过完整性校验"
    }
    Expand-Archive -Path $zip -DestinationPath $BinDir -Force
    Remove-Item $zip -Force -ErrorAction SilentlyContinue
    if (-not (Test-Path $OpenListExe)) { Die "解压后找不到 openlist.exe" }
    Log "OpenList 就绪"
}

# ================= 4. OpenList 管理员密码 =================
if (Test-Path $OLAdminFile) {
    $OLPass = ((Get-Content $OLAdminFile | Where-Object { $_ -match '^密码: ' }) -replace '^密码: ', '').Trim()
    Log "复用已有管理员凭据：$OLAdminFile"
} else {
    $OLPass = (New-RandomBase64 15) -replace '[+/=]', ''
    Set-Content -Path $OLAdminFile -Value ("用户: admin`r`n密码: $OLPass") -Encoding UTF8
    Protect-File $OLAdminFile
    Log "管理员密码已生成：$OLAdminFile"
}
if (-not $OLPass) { Die "读取 OpenList 管理员密码失败" }

# ================= 5. 启动 OpenList =================
if (Test-Port $OpenListPort) {
    Log "OpenList 已在 :$OpenListPort 监听，跳过启动"
} else {
    Log "启动 OpenList（数据目录 $OLData）"
    # 新版 OpenList 首启不会自动建 admin，改为通过环境变量直接在启动时创建，
    # 避免依赖已不存在的 /api/public/init/setup 接口。
    $procId = Start-Detached "cmd.exe /c set OPENLIST_ADMIN_PASSWORD=$OLPass&& `"$OpenListExe`" server --data `"$OLData`"" $Root
    Wait-Port $OpenListPort 40 'OpenList'
    $opPid = (Get-Process 'openlist' -ErrorAction SilentlyContinue | Select-Object -First 1).Id
    Set-Content (Join-Path $RunDir 'openlist.pid') ($(if ($opPid) { $opPid } else { $procId }))
    Log "OpenList 已启动（PID $opPid，http://127.0.0.1:$OpenListPort）"
}

# v4.2.9 起首启不再自动创建 admin（改为 Web 安装向导）：未初始化则调 API 创建
try {
    $st = Invoke-RestMethod -Uri "http://127.0.0.1:$OpenListPort/api/public/init/status" -TimeoutSec 10 -ErrorAction Stop
    if ($st.data -and ($st.data.initialized -eq $false)) {
        Log "首次运行，通过安装向导 API 创建管理员"
        $r = Invoke-RestMethod -Uri "http://127.0.0.1:$OpenListPort/api/public/init/setup" -Method Post `
            -ContentType 'application/json' -TimeoutSec 15 -ErrorAction Stop `
            -Body (@{ username = 'admin'; password = $OLPass; site_title = 'BaiduVault' } | ConvertTo-Json)
        if ($r.code -ne 200) { Die "初始化管理员失败：$($r.message)" }
        Log "管理员已创建"
    }
} catch { Warn "初始化状态检查异常：$($_.Exception.Message)（若后续登录正常可忽略）" }

# ================= 6. 登录 API + 补 WebDAV 写权限位 =================
Log "登录 OpenList API"
try {
    $login = Invoke-RestMethod -Uri "http://127.0.0.1:$OpenListPort/api/auth/login" -Method Post `
        -ContentType 'application/json' -Body (@{ username = 'admin'; password = $OLPass } | ConvertTo-Json) -TimeoutSec 15 -ErrorAction Stop
} catch { Die "登录 API 调用失败：$($_.Exception.Message)" }
if (-not $login -or $login.code -ne 200) { Die "OpenList 登录失败（code=$($login.code)）。若你手工改过密码，请同步更新 $OLAdminFile" }
$Token = $login.data.token
$H = @{ Authorization = $Token }

$users = Invoke-RestMethod -Uri "http://127.0.0.1:$OpenListPort/api/admin/user/list" -Headers $H -TimeoutSec 15
$admin = @($users.data.content | Where-Object { $_.username -eq 'admin' })[0]
if ($admin) {
    if (($admin.permission -band 512) -ne 0) {
        Log "WebDAV 写权限位已存在（permission=$($admin.permission)）"
    } else {
        $newPerm = $admin.permission -bor 512
        $body = @{ id = $admin.id; username = $admin.username; role = $admin.role; permission = $newPerm; base_path = $admin.base_path } | ConvertTo-Json
        $r = Invoke-RestMethod -Uri "http://127.0.0.1:$OpenListPort/api/admin/user/update" -Method Post -Headers $H -ContentType 'application/json' -Body $body -TimeoutSec 15
        if ($r.code -eq 200) { Log "已补 WebDAV 写权限位：$($admin.permission) → $newPerm" }
        else { Warn "权限位修正返回 code=$($r.code)，若后续 move 自检 403：后台 → 用户 → admin → 勾「WebDAV 写入」" }
    }
} else { Warn "读不到 admin 用户信息，跳过权限位修正" }

# ================= 7. 添加存储（百度 / 演示本地）=================
$stor = Invoke-RestMethod -Uri "http://127.0.0.1:$OpenListPort/api/admin/storage/list" -Headers $H -TimeoutSec 15
$baiduMount = @($stor.data.content | Where-Object { $_.mount_path -eq '/baidu' }) | Select-Object -First 1
$hasBaiduMount = [bool]$baiduMount

# 已有 /baidu 是本地演示存储、而这次给了百度参数 → 自动替换为百度网盘
if ($hasBaiduMount -and $baiduMount.driver -eq 'Local' -and $BaiduRefreshToken -and $BaiduClientId -and $BaiduClientSecret) {
    Log "检测到 /baidu 是本地演示存储，替换为百度网盘"
    $r = Invoke-RestMethod -Uri "http://127.0.0.1:$OpenListPort/api/admin/storage/delete?id=$($baiduMount.id)" -Method Post -Headers $H -TimeoutSec 15
    if ($r.code -eq 200) { $hasBaiduMount = $false } else { Die "删除演示存储失败：$($r.message)。请到后台手工删除后重跑。" }
}

if ($hasBaiduMount) {
    Log "挂载路径 /baidu 已存在（驱动 $($baiduMount.driver)），跳过添加存储"
} elseif ($BaiduRefreshToken -and $BaiduClientId -and $BaiduClientSecret) {
    Log "添加百度网盘存储（挂载 /baidu，根 $BaiduRootFolder）"
    $addition = @{
        root_folder_path   = $BaiduRootFolder
        refresh_token      = $BaiduRefreshToken
        client_id          = $BaiduClientId
        client_secret      = $BaiduClientSecret
        use_online_api     = $false          # 不经过第三方服务器刷新 token
        download_api       = 'official'
        custom_crack_ua    = 'netdisk'
        upload_thread      = '3'
        upload_timeout     = 60
        use_dynamic_upload_api = $true
        custom_upload_part_size = 0
        low_bandwith_upload_mode = $false
        only_list_video_file   = $false
    } | ConvertTo-Json -Compress
    $body = @{
        mount_path = '/baidu'; order = 0; driver = 'BaiduNetdisk'
        cache_expiration = 30; status = 'work'; web_proxy = $false
        webdav_policy = 'native_proxy'; down_proxy_url = ''
        order_by = 'name'; order_direction = 'asc'; extract_folder = ''
        enable_sign = $false; disable_index = $false; addition = $addition
    } | ConvertTo-Json -Depth 4
    $r = Invoke-RestMethod -Uri "http://127.0.0.1:$OpenListPort/api/admin/storage/create" -Method Post -Headers $H -ContentType 'application/json' -Body $body -TimeoutSec 20
    if ($r.code -eq 200) { Log "百度网盘存储已添加（后台约 1 分钟内完成初始化）" }
    else { Die "添加百度存储失败：$($r.message)。可去 http://127.0.0.1:$OpenListPort 后台手工添加后重跑本脚本。" }
} elseif ($DemoLocal) {
    Log "添加本地演示存储（挂载 /baidu → $LocalStore）"
    $addition = @{ root_folder_path = ($LocalStore -replace '\\', '/'); show_hidden = $true; mkdir_perm = '777'; thumbnail = $false } | ConvertTo-Json -Compress
    $body = @{
        mount_path = '/baidu'; order = 0; driver = 'Local'
        cache_expiration = 30; status = 'work'; web_proxy = $false
        webdav_policy = 'native_proxy'; down_proxy_url = ''
        order_by = 'name'; order_direction = 'asc'; extract_folder = ''
        enable_sign = $false; disable_index = $false; addition = $addition
    } | ConvertTo-Json -Depth 4
    $r = Invoke-RestMethod -Uri "http://127.0.0.1:$OpenListPort/api/admin/storage/create" -Method Post -Headers $H -ContentType 'application/json' -Body $body -TimeoutSec 20
    if ($r.code -eq 200) { Log "本地演示存储已添加（仅用于先把链路跑通；接入百度后请在后台删除它）" }
    else { Die "添加演示存储失败：$($r.message)" }
} else {
    Warn "未提供百度参数、也未指定 -DemoLocal，跳过添加存储。"
    Warn "接入百度网盘需人工操作（约 10 分钟），步骤见「使用说明.md」第 3 节；完成后重跑本脚本即可。"
}

# 7b. 额外本地演示库（各自独立目录、独立密钥）
foreach ($lib in $DemoLibraries) {
    $name = $lib.Trim().Trim('/')
    if (-not ($name -match '^[A-Za-z0-9_]+$')) { Warn "库名 '$lib' 非法（仅允许字母数字下划线），已跳过"; continue }
    $mp = "/$name"
    $exist = @($stor.data.content | Where-Object { $_.mount_path -eq $mp }) | Select-Object -First 1
    if ($exist) { Log "挂载路径 $mp 已存在（驱动 $($exist.driver)），跳过"; continue }
    $storeDir = Join-Path $Root "local-store-$name"
    if (-not (Test-Path $storeDir)) { New-Item -ItemType Directory -Force $storeDir | Out-Null }
    Log "添加本地演示存储（挂载 $mp → $storeDir）"
    $addition = @{ root_folder_path = ($storeDir -replace '\\', '/'); show_hidden = $true; mkdir_perm = '777'; thumbnail = $false } | ConvertTo-Json -Compress
    $body = @{
        mount_path = $mp; order = 0; driver = 'Local'
        cache_expiration = 30; status = 'work'; web_proxy = $false
        webdav_policy = 'native_proxy'; down_proxy_url = ''
        order_by = 'name'; order_direction = 'asc'; extract_folder = ''
        enable_sign = $false; disable_index = $false; addition = $addition
    } | ConvertTo-Json -Depth 4
    $r = Invoke-RestMethod -Uri "http://127.0.0.1:$OpenListPort/api/admin/storage/create" -Method Post -Headers $H -ContentType 'application/json' -Body $body -TimeoutSec 20
    if ($r.code -eq 200) { Log "演示库 $name 已添加" } else { Warn "添加演示库 $name 失败：$($r.message)，已跳过" }
}

# ================= 8. OpenList WebDAV 探测 =================
$code = & curl.exe -s -o NUL -w '%{http_code}' -X PROPFIND -H 'Depth: 0' -u "admin:$OLPass" --max-time 10 "http://127.0.0.1:$OpenListPort/dav/"
if ($code -eq '207') { Log "OpenList WebDAV /dav 正常（HTTP 207）" }
else { Die "OpenList WebDAV 异常（HTTP $code）。请确认 OpenList 正常运行：$LogDir\openlist-*.log" }

# ================= 9. 汇总库清单 =================
# 库 = OpenList 的一个挂载路径。来源三路并集：既有 conf 的库 ∪ OpenList 现有挂载 ∪ -DemoLibraries。
# 挂载已被删除的旧库自动剔除（数据仍在网盘，重新挂载后重跑本脚本即恢复）。
$stor = Invoke-RestMethod -Uri "http://127.0.0.1:$OpenListPort/api/admin/storage/list" -Headers $H -TimeoutSec 15
$mountSet = @{}
foreach ($s in @($stor.data.content)) { if ($s) { $mountSet[$s.mount_path.Trim('/')] = $true } }

$libNames = [System.Collections.Generic.List[string]]@()
function Add-Lib([string]$n) {
    $n = $n.Trim().Trim('/')
    if ($n -and ($n -match '^[A-Za-z0-9_]+$') -and (-not $script:libNames.Contains($n))) { $script:libNames.Add($n) }
}
if (Test-Path $Conf) {
    foreach ($m in [regex]::Matches((Get-Content $Conf -Raw), '(?m)^\[(\w+)_crypt\]')) {
        $n = $m.Groups[1].Value
        if ($mountSet.ContainsKey($n)) { Add-Lib $n }
        else { Warn "库 $n 的挂载 /$n 已不存在，从配置剔除（数据仍在网盘，重挂后重跑 setup 自动恢复）" }
    }
}
foreach ($k in $mountSet.Keys) { Add-Lib $k }
foreach ($n in $DemoLibraries) { Add-Lib $n }
if ($libNames.Count -eq 0) { Add-Lib 'baidu' }   # 尚无存储时先占位，接入后重跑自动生效
Log ("库清单：" + ($libNames -join ', '))

# ================= 10. 每库 crypt 密钥（复用 > 从旧 conf 迁移 > 新生成） =================
$keyMap = @{}
if (Test-Path $KeysFile) {
    foreach ($line in (Get-Content $KeysFile)) {
        if ($line -match '^([A-Za-z0-9_]+)=(.*)$') { $keyMap[$Matches[1]] = $Matches[2].Trim() }
    }
}
$confRawOld = if (Test-Path $Conf) { Get-Content $Conf -Raw } else { '' }
$libKeys = @{}
foreach ($lib in $libNames) {
    $prefix = if ($lib -eq 'baidu') { '' } else { $lib.ToUpper() + '_' }
    $o1 = $keyMap["${prefix}CRYPT_PASS_OBSCURED"]; $o2 = $keyMap["${prefix}CRYPT_PASS2_OBSCURED"]
    if ($o1 -and $o2) {
        $libKeys[$lib] = @{ O1 = $o1; O2 = $o2; P1 = $keyMap["${prefix}CRYPT_PASS"]; P2 = $keyMap["${prefix}CRYPT_PASS2"] }
        Log "库 ${lib}：复用已有密钥"
        continue
    }
    $block = [regex]::Match($confRawOld, "(?ms)^\[${lib}_crypt\]\s*?(.*?)(?=^\[|\z)")
    $pm1 = [regex]::Match($block.Groups[1].Value, '(?m)^password\s*=\s*(\S+)')
    $pm2 = [regex]::Match($block.Groups[1].Value, '(?m)^password2\s*=\s*(\S+)')
    if ($pm1.Success -and $pm2.Success) {
        $libKeys[$lib] = @{ O1 = $pm1.Groups[1].Value; O2 = $pm2.Groups[1].Value; P1 = ''; P2 = '' }
        Log "库 ${lib}：密钥从既有 rclone.conf 迁移（明文未记录）"
        continue
    }
    $p1n = New-RandomBase64 24; $p2n = New-RandomBase64 24
    $libKeys[$lib] = @{ O1 = (& $RcloneExe obscure "$p1n").Trim(); O2 = (& $RcloneExe obscure "$p2n").Trim(); P1 = $p1n; P2 = $p2n }
    Log "库 ${lib}：已生成新密钥"
}

# 重写密钥文件（全量、分库分节；内容不变则不落盘，保持幂等）
$kb = [System.Text.StringBuilder]::new()
[void]$kb.AppendLine('# rclone crypt 密钥 — 每库一套（分库分节），丢失即该库数据永久不可读，请立即离线备份（U盘/另一台机器）')
[void]$kb.AppendLine('# 由 setup-local.ps1 维护；格式：<库名大写>_CRYPT_PASS_OBSCURED（baidu 库无前缀，保持兼容）')
foreach ($lib in $libNames) {
    $k = $libKeys[$lib]; $prefix = if ($lib -eq 'baidu') { '' } else { $lib.ToUpper() + '_' }
    [void]$kb.AppendLine('')
    [void]$kb.AppendLine("# === 库: $lib ===")
    if ($k.P1) { [void]$kb.AppendLine("${prefix}CRYPT_PASS=$($k.P1)") } else { [void]$kb.AppendLine("# ${prefix}CRYPT_PASS=（明文未记录：从既有配置迁移而来）") }
    if ($k.P2) { [void]$kb.AppendLine("${prefix}CRYPT_PASS2=$($k.P2)") } else { [void]$kb.AppendLine("# ${prefix}CRYPT_PASS2=（同上）") }
    [void]$kb.AppendLine("${prefix}CRYPT_PASS_OBSCURED=$($k.O1)")
    [void]$kb.AppendLine("${prefix}CRYPT_PASS2_OBSCURED=$($k.O2)")
}
$keysNew = $kb.ToString()
$keysOld = if (Test-Path $KeysFile) { (Get-Content $KeysFile -Raw) -replace '\r\n', "`n" } else { '' }
if (($keysNew -replace '\r\n', "`n") -cne $keysOld) {
    # WriteAllText 原样落盘（Set-Content 会多补一个尾换行，导致每次比较都不同）
    [System.IO.File]::WriteAllText($KeysFile, $keysNew, [System.Text.UTF8Encoding]::new($true))
    Protect-File $KeysFile
    Log "密钥文件已更新：$KeysFile（请立刻离线备份）"
} else { Log '密钥文件无变化' }

# ================= 11. 写 rclone.conf（每库 crypt+chunker + [vault] combine） =================
# opl 的 pass 每次 obscure 都会变（随机 nonce），直接重算会让 conf 永远"有变化"。
# 策略：从既有 conf 收割 pass 并实测仍有效才复用；失效（管理员密码被换）才重新 obscure。
$OL_OBSCURED = ''
$oplBlock = [regex]::Match($confRawOld, '(?ms)^\[opl\]\s*?(.*?)(?=^\[|\z)')
$oplPassM = [regex]::Match($oplBlock.Groups[1].Value, '(?m)^pass\s*=\s*(\S+)')
if ($oplPassM.Success) {
    & $RcloneExe --config $Conf lsd 'opl:' --max-depth 1 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) { $OL_OBSCURED = $oplPassM.Groups[1].Value }
    else { Log '既有 conf 的 OpenList pass 已失效，重新生成混淆串' }
}
if (-not $OL_OBSCURED) { $OL_OBSCURED = (& $RcloneExe obscure "$OLPass").Trim() }
$script:ConfChanged = $false
function Build-ConfText([System.Collections.Generic.List[string]]$libs) {
    $cb = [System.Text.StringBuilder]::new()
    [void]$cb.AppendLine('# 链路：OpenList(WebDAV) → 每库 crypt → chunker → [vault] combine    由 setup-local.ps1 生成，可重跑刷新')
    [void]$cb.AppendLine('[opl]')
    [void]$cb.AppendLine('type = webdav')
    [void]$cb.AppendLine("url = http://127.0.0.1:$OpenListPort/dav")
    [void]$cb.AppendLine('vendor = other')
    [void]$cb.AppendLine('user = admin')
    [void]$cb.AppendLine("pass = $OL_OBSCURED")
    [void]$cb.AppendLine('')
    foreach ($lib in $libs) {
        $k = $libKeys[$lib]
        [void]$cb.AppendLine("# ----- 库: $lib（底层 = OpenList 挂载 /$lib 下的 secret_vault）-----")
        [void]$cb.AppendLine("[${lib}_crypt]")
        [void]$cb.AppendLine('type = crypt')
        [void]$cb.AppendLine("remote = opl:/${lib}/secret_vault")
        [void]$cb.AppendLine('filename_encryption = standard')
        [void]$cb.AppendLine('directory_name_encryption = true')
        [void]$cb.AppendLine('suffix = none')
        [void]$cb.AppendLine("password = $($k.O1)")
        [void]$cb.AppendLine("password2 = $($k.O2)")
        [void]$cb.AppendLine('')
        [void]$cb.AppendLine("[${lib}_chunk512]")
        [void]$cb.AppendLine('type = chunker')
        [void]$cb.AppendLine("remote = ${lib}_crypt:")
        [void]$cb.AppendLine('chunk_size = 512M')
        [void]$cb.AppendLine('name_format = *.rcc##')
        [void]$cb.AppendLine('')
    }
    $ups = ($libs | ForEach-Object { "${_}=${_}_chunk512:" }) -join ' '
    [void]$cb.AppendLine('# 聚合层：单 serve 进程单端口，URL 路径前缀即库名')
    [void]$cb.AppendLine('[vault]')
    [void]$cb.AppendLine('type = combine')
    [void]$cb.AppendLine("upstreams = $ups")
    return $cb.ToString()
}
function Save-Conf([string]$text) {
    [System.IO.File]::WriteAllText($Conf, $text, [System.Text.UTF8Encoding]::new($true))
    Protect-File $Conf
    $script:ConfChanged = $true
}
$confNew = Build-ConfText $libNames
if (($confNew -replace '\r\n', "`n") -cne ($confRawOld -replace '\r\n', "`n")) {
    Save-Conf $confNew
    Log "rclone 配置已写入 $Conf（库：$($libNames -join ', ')）"
} else { Log 'rclone 配置无变化' }

# ================= 12. server-side move 自检（chunker 硬前提，逐库） =================
if ($mountSet.Count -gt 0) {
    $badLibs = @()
    foreach ($lib in @($libNames)) {
        if (-not $mountSet.ContainsKey($lib)) { continue }   # 占位库（存储未接入）跳过
        $tag = ".move_test_$PID"
        & $RcloneExe --config $Conf mkdir "${lib}_crypt:" 2>$null | Out-Null
        & $RcloneExe --config $Conf touch "${lib}_crypt:/$tag" 2>$null | Out-Null
        & $RcloneExe --config $Conf moveto "${lib}_crypt:/$tag" "${lib}_crypt:/.move_done_$PID" 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) {
            & $RcloneExe --config $Conf delete "${lib}_crypt:/.move_done_$PID" 2>$null | Out-Null
            Log "库 ${lib}：move 支持正常，chunker 可用"
        } else {
            & $RcloneExe --config $Conf delete "${lib}_crypt:/$tag" 2>$null | Out-Null
            Warn "库 ${lib}：底层不支持 server-side move，本次从配置剔除（修复后重跑 setup 自动恢复）"
            $badLibs += $lib
        }
    }
    if ($badLibs.Count -gt 0) {
        $libNames = [System.Collections.Generic.List[string]]@($libNames | Where-Object { $badLibs -notcontains $_ })
        if ($libNames.Count -eq 0) {
            Die '所有库都不支持 server-side move。若是 403：OpenList 后台 → 用户 → admin → 勾「WebDAV 写入」；若存储刚添加，等 1 分钟重跑本脚本。'
        }
        Save-Conf (Build-ConfText $libNames)
        Log "已按可用库重写配置：$($libNames -join ', ')"
    }
} else {
    Warn '尚无存储，跳过 move 自检（接入存储后重跑本脚本会自动补上）'
}

# ================= 13. WebDAV 播放凭据 =================
if (Test-Path $CredFile) {
    Log "复用已有播放凭据：$CredFile"
} else {
    $WPass = (New-RandomBase64 18) -replace '[+/=]', ''
    Set-Content -Path $CredFile -Value ("WEBDAV_USER=media`r`nWEBDAV_PASS=$WPass") -Encoding UTF8
    Protect-File $CredFile
    Log "播放凭据已生成：$CredFile"
}
$WUser = ((Get-Content $CredFile | Where-Object { $_ -match '^WEBDAV_USER=' }) -replace '^[^=]*=', '').Trim()
$WPass = ((Get-Content $CredFile | Where-Object { $_ -match '^WEBDAV_PASS=' }) -replace '^[^=]*=', '').Trim()

# ================= 14. 启动 rclone serve webdav（vault: 聚合根） =================
$needStart = $true
if (Test-Port $WebDavPort) {
    if (-not $ConfChanged) {
        Log "WebDAV 已在 :$WebDavPort 监听且配置未变，跳过启动"
        $needStart = $false
    } else {
        Log "配置已变更，重启 rclone serve 加载新配置"
        foreach ($pf in @('rclone-serve.pid', 'rclone-webdav.pid')) {
            $pidFile = Join-Path $RunDir $pf
            if (Test-Path $pidFile) {
                $oldPid = 0
                [int]::TryParse((Get-Content $pidFile | Select-Object -First 1).Trim(), [ref]$oldPid) | Out-Null
                if ($oldPid -gt 0) { Stop-Process -Id $oldPid -Force -ErrorAction SilentlyContinue }
                Remove-Item $pidFile -Force -ErrorAction SilentlyContinue
            }
        }
        $freed = $false
        for ($i = 0; $i -lt 15; $i++) { if (-not (Test-Port $WebDavPort)) { $freed = $true; break }; Start-Sleep -Seconds 1 }
        if (-not $freed) { Die ":$WebDavPort 仍被占用（可能不是本套件的进程）。请先跑 stop.ps1，或 netstat -ano | findstr :$WebDavPort 排查。" }
    }
}
if ($needStart) {
    Log "启动 rclone serve webdav（127.0.0.1:$WebDavPort，根 = vault: 聚合层）"
    $args = @(
        '--config', "`"$Conf`"",
        'serve', 'webdav', 'vault:',
        '--addr', "127.0.0.1:$WebDavPort",
        '--user', $WUser, '--pass', $WPass,
        '--vfs-cache-mode', 'full',
        '--cache-dir', "`"$CacheDir`"",
        '--vfs-cache-max-size', '10G',
        '--vfs-cache-max-age', '48h',
        '--vfs-read-ahead', '64M',
        '--buffer-size', '8M',
        '--vfs-read-chunk-size', '16M',
        '--vfs-read-chunk-size-limit', '256M',
        '--dir-cache-time', '1m',
        '--rc', '--rc-addr', '127.0.0.1:5572', '--rc-no-auth',
        '--chunker-fail-hard',
        '--log-level', 'INFO',
        '--log-file', "`"$LogDir\rclone-webdav.log`""
    )
    $cmdLine = "`"$RcloneExe`" " + ($args -join ' ')
    $procId = Start-Detached $cmdLine $Root
    Set-Content (Join-Path $RunDir 'rclone-serve.pid') $procId
    Remove-Item (Join-Path $RunDir 'rclone-webdav.pid') -Force -ErrorAction SilentlyContinue
    Wait-Port $WebDavPort 30 'rclone serve webdav'
    Log "WebDAV 已启动（PID $procId）"
}

# ================= 完成 =================
Write-Host ''
Log "部署完成。凭据/配置位置："
Write-Host "  crypt 密钥   : $KeysFile   ← 立刻离线备份，丢失后云端数据不可恢复"
Write-Host "  OpenList 管理: $OLAdminFile"
Write-Host "  播放凭据     : $CredFile"
Write-Host "  rclone 配置  : $Conf"
Write-Host ''
Write-Host "播放：PotPlayer → 打开 URL → http://127.0.0.1:$WebDavPort/<库名>/ （当前库：$($libNames -join ', ')），用户名 $WUser，密码见上面播放凭据文件"
if (-not ($hasBaiduMount -or $BaiduRefreshToken -or $DemoLocal)) {
    Write-Host ''
    Warn "还差最后一步：人工接入百度网盘（见「使用说明.md」第 3 节），完成后重跑本脚本。"
} else {
    Write-Host "验收：powershell -ExecutionPolicy Bypass -File .\verify.ps1 -Target '/某个已上传文件.mkv'（默认库 baidu；其他库加 -Library 库名）"
}
