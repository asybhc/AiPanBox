<#
.SYNOPSIS
    上传文件/目录到加密区，并主动刷新播放端缓存

.DESCRIPTION
    写入由独立的 rclone copy 进程完成，而对外提供播放的 rclone serve 进程有自己的
    目录缓存，感知不到别的进程写入。传完必须让 serve 失效对应目录，否则播放端会
    按旧路径取文件、报 404。

.PARAMETER Source
    本地文件或目录路径（必填）

.PARAMETER Dest
    加密区内的目标**目录**（相对根目录，默认 /）。
    注意：rclone copy 把 Dest 当目录处理——传单个文件时 Dest 填目录（如 '/'），不是文件名。

.PARAMETER Library
    目标库名（默认 baidu）。写入 <库名>_chunk512，播放路径为 /<库名>/…

.PARAMETER Transfers
    并发数（默认 1；百度接口限速约 4.65MB/s，开多并发意义有限）

.PARAMETER DryRun
    空跑预估，不实际上传、不刷新缓存

.EXAMPLE
    .\upload.ps1 -Source 'D:\片库\某电影.mkv' -Dest '/'
    .\upload.ps1 -Source 'D:\片库\某剧集' -Dest '/某剧集'
    .\upload.ps1 -Source 'D:\片库' -Dest '/' -DryRun
    .\upload.ps1 -Source 'D:\片库' -Dest '/' -Transfers 2
    .\upload.ps1 -Source 'D:\片库' -Dest '/' -Library demo2
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Source,
    [Parameter(Position = 1)]
    [string]$Dest = '/',
    [string]$Library = 'baidu',
    [int]$Transfers = 1,
    [switch]$DryRun
)

# PS5.1 下 rclone 的 --progress 写 stderr，EAP=Stop 会被误判为终止错误，故用 Continue + $LASTEXITCODE
$ErrorActionPreference = 'Continue'

$Root      = Join-Path $env:LOCALAPPDATA 'BaiduVault'
$RcloneExe = Join-Path $Root 'bin\rclone.exe'
$Conf      = Join-Path $Root 'rclone.conf'
$RcAddr    = if ($env:RC_ADDR) { $env:RC_ADDR } else { 'http://127.0.0.1:5572' }
$Remote    = "${Library}_chunk512"

if (-not (Test-Path $RcloneExe)) { Write-Host "[FAIL] 未部署：$RcloneExe 不存在，请先运行 setup-local.ps1" -ForegroundColor Red; exit 1 }
if (-not (Test-Path $Conf))      { Write-Host "[FAIL] 未部署：$Conf 不存在，请先运行 setup-local.ps1" -ForegroundColor Red; exit 1 }
if (-not (Test-Path $Source))    { Write-Host "[FAIL] 源路径不存在: $Source" -ForegroundColor Red; exit 1 }
if (-not $Dest.StartsWith('/'))  { $Dest = "/$Dest" }

$confText = Get-Content $Conf -Raw
if ($confText -notmatch "\[${Library}_chunk512\]") {
    $avail = [regex]::Matches($confText, '(?m)^\[(\w+)_chunk512\]') | ForEach-Object { $_.Groups[1].Value }
    Write-Host "[FAIL] 配置里没有库 '$Library'（当前可用：$($avail -join ', ')）。加库：OpenList 后台加存储后重跑 setup-local.ps1" -ForegroundColor Red; exit 1
}

Write-Host "库    : $Library"
Write-Host "源    : $Source"
Write-Host "目标  : ${Remote}:$Dest"
Write-Host "并发  : $Transfers"
if ($DryRun) { Write-Host "模式  : dry-run（不实际上传）" }
Write-Host ''

$start = Get-Date
$rcloneArgs = @('--config', $Conf, 'copy', $Source, "${Remote}:$Dest", '--transfers', "$Transfers", '--progress')
if ($DryRun) { $rcloneArgs += '--dry-run' }
& $RcloneExe @rcloneArgs
if ($LASTEXITCODE -ne 0) { Write-Host "[FAIL] rclone copy 失败（退出码 $LASTEXITCODE）" -ForegroundColor Red; exit $LASTEXITCODE }

$cost = [int]((Get-Date) - $start).TotalSeconds
Write-Host ''
Write-Host "传输完成，耗时 ${cost}s"

if ($DryRun) { Write-Host "（dry-run，不刷新缓存）"; exit 0 }

# 刷新播放端目录缓存（serve 根是 vault: 聚合层，VFS 内路径要带库前缀）
$vfsDir = "/$Library$Dest"
$enc = [System.Uri]::EscapeDataString($vfsDir)
try {
    $null = Invoke-RestMethod -Uri "$RcAddr/vfs/forget?dir=$enc" -Method Post -TimeoutSec 8 -ErrorAction Stop
    Write-Host "缓存已刷新: $vfsDir  → 可以立即播放"
} catch {
    Write-Host "[WARN] 调用 $RcAddr/vfs/forget 失败，缓存没有刷新。" -ForegroundColor Yellow
    Write-Host "  可能原因：rclone serve 未运行（先跑 setup-local.ps1 或 stop.ps1 后再 setup）"
    Write-Host "  临时办法：等 --dir-cache-time 过期（当前 1m），或重跑 setup-local.ps1 重启播放服务"
    exit 2
}

Write-Host ''
Write-Host '远端大小：'
& $RcloneExe --config $Conf size "${Remote}:$Dest" 2>$null
Write-Host "播放路径：http://127.0.0.1:8088$vfsDir"
