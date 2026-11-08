<#
.SYNOPSIS
    停止 BaiduVault 的两个后台进程（OpenList + rclone serve），并报告端口释放情况。

.DESCRIPTION
    只停 %LOCALAPPDATA%\BaiduVault\run\*.pid 记录的进程，以及 bin\ 目录下的
    openlist.exe / rclone.exe 进程（按可执行文件路径匹配，不会误伤别的 rclone）。
    不删除任何数据；重新启动用 setup-local.ps1（幂等）。
#>
$Root = Join-Path $env:LOCALAPPDATA 'BaiduVault'
$RunDir = Join-Path $Root 'run'
$BinDir = Join-Path $Root 'bin'
$stopped = 0

function Stop-One([string]$Name, [int]$ProcId) {
    $p = Get-Process -Id $ProcId -ErrorAction SilentlyContinue
    if ($p) {
        Stop-Process -Id $ProcId -Force -ErrorAction SilentlyContinue
        Write-Host "已停止 $Name（PID $ProcId）"
        $script:stopped++
    }
}

# 1) 按 pid 文件停
foreach ($f in @('openlist.pid', 'rclone-serve.pid', 'rclone-webdav.pid')) {
    $pf = Join-Path $RunDir $f
    if (Test-Path $pf) {
        $procId = 0
        [int]::TryParse((Get-Content $pf -ErrorAction SilentlyContinue | Select-Object -First 1).Trim(), [ref]$procId) | Out-Null
        if ($procId -gt 0) { Stop-One ($f -replace '\.pid$', '') $procId }
        Remove-Item $pf -Force -ErrorAction SilentlyContinue
    }
}

# 2) 兜底：按可执行文件路径找漏网进程
foreach ($exe in @('openlist.exe', 'rclone.exe')) {
    $path = Join-Path $BinDir $exe
    Get-Process -Name ($exe -replace '\.exe$', '') -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $path } |
        ForEach-Object { Stop-One $exe $_.Id }
}

Start-Sleep -Seconds 1
foreach ($port in @(5244, 8088)) {
    $listening = $false
    try {
        $c = New-Object System.Net.Sockets.TcpClient
        $iar = $c.BeginConnect('127.0.0.1', $port, $null, $null)
        if ($iar.AsyncWaitHandle.WaitOne(500) -and $c.Connected) { $listening = $true }
        $c.Close()
    } catch {}
    if ($listening) { Write-Host "[WARN] 端口 :$port 仍在监听（可能有别的实例占用）" -ForegroundColor Yellow }
    else { Write-Host "端口 :$port 已释放" }
}

if ($stopped -eq 0) { Write-Host "没有发现运行中的 BaiduVault 进程" }
Write-Host "完成。数据与配置保留在 $Root；重新启动：setup-local.ps1"
