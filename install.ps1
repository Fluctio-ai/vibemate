# VibeMate 一键安装（由 一键安装.bat 双击调起；也可右键「使用 PowerShell 运行」）
# 作用：一次 UAC → 建最高权限登录计划任务 → 启动程序 → 验证。
# 建任务走 exe 自己的 --setup-task（schtasks 参数只在 HttpServer 存一份）。

# ---------- 0) 前置检查（放在提权前：exe 不在就别白弹 UAC）----------
$exe = Join-Path $PSScriptRoot 'VibeMate.exe'
if (-not (Test-Path $exe)) {
    Write-Host "[错误] 找不到 $exe —— 本脚本必须和 VibeMate.exe 放在同一文件夹里。" -ForegroundColor Red
    Read-Host '按回车键退出'
    exit 1
}

# ---------- 1) 自提权（唯一一次 UAC）----------
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host '需要管理员权限（建开机自启任务）—— 正在请求，请在弹窗中点「是」...'
    Start-Process powershell -Verb RunAs -Wait -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    exit
}

Write-Host '=== VibeMate 一键安装 ==='
Write-Host ''

# ---------- 2) 建计划任务（登录自启 + 最高权限；强制覆盖，重复运行安全）----------
Write-Host '[1/4] 创建开机自启计划任务（最高权限）...'
# 提权上下文里 exe 继承管理员权限，内部不再弹 UAC
& $exe --setup-task | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host '[错误] 计划任务创建失败' -ForegroundColor Red
    Read-Host '按回车键退出'
    exit 1
}

# ---------- 3) 停旧实例（若有；管理员下可杀提权进程）----------
Write-Host '[2/4] 停止旧实例（若有）...'
Get-Process VibeMate -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

# ---------- 4) 启动 ----------
Write-Host '[3/4] 启动 VibeMate...'
schtasks /Run /TN VibeMate | Out-Null

# ---------- 5) 验证（HTTP 就绪即成功；刚起的监听器可能晚一两秒）----------
Write-Host '[4/4] 验证服务...'
$ok = $false
$ver = ''
for ($i = 0; $i -lt 10; $i++) {
    Start-Sleep -Seconds 1
    try {
        $r = Invoke-RestMethod 'http://127.0.0.1:8787/api/ping' -TimeoutSec 2
        $ok = $true; $ver = $r.version; break
    } catch {}
}

Write-Host ''
if ($ok) {
    Write-Host "安装成功！版本 $ver" -ForegroundColor Green
    Write-Host ''
    Write-Host '· 右下角托盘出现 VibeMate 图标（闭眼呼吸 = 待机）'
    Write-Host '· 左键点图标打开设置页：http://127.0.0.1:8787'
    Write-Host '· 下一步：设置页选择你的遥控器（详见设置页的「安装帮助」）'
    Write-Host '· 虚拟声卡 VB-CABLE 若未安装，程序会自动静默装（管理员权限下无需干预）'
} else {
    Write-Host '服务没有响应 —— 请看本文件夹里的 vibe.log 排障（或重新运行本脚本）' -ForegroundColor Yellow
}
Read-Host '按回车键关闭'
