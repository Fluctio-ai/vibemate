# VibeMate v2 one-shot deploy（本机开发部署入口；发布/CI 见 build.yml）
# 计划任务的创建统一走 exe 自己的 --setup-task —— schtasks 参数只在 HttpServer 存一份
$ErrorActionPreference = 'Continue'
# ★ 全部绝对路径：计划任务存相对路径时手动 /Run 碰巧能解析、登录自启会静默失败
$proj = Join-Path $PSScriptRoot 'VibeMate'
$rel  = Join-Path $PSScriptRoot 'release'
$exe  = Join-Path $rel 'VibeMate.exe'
$tn   = 'VibeMate'

# 1) 计划任务：路径不对才强制重建（无条件重建会让每次部署都弹 UAC）
$taskXml = schtasks /Query /TN $tn /XML 2>$null
if ($null -eq $taskXml -or ($taskXml -join ' ') -notmatch [regex]::Escape($exe)) {
    Write-Host "[task] (re)creating scheduled task -> $exe (ONE UAC)..."
    Start-Process $exe -Verb RunAs -Wait -ArgumentList '--setup-task'
}

# 2) 停进程：/End 异步、管理员进程普通 Stop 杀不动 —— 循环等真消失（防 publish 文件锁）
schtasks /End /TN $tn 2>$null | Out-Null
Get-Process VibeMate -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
for ($i = 0; $i -lt 15; $i++) {
    if (-not (Get-Process VibeMate -ErrorAction SilentlyContinue)) { break }
    Start-Sleep -Seconds 1
}
if (Get-Process VibeMate -ErrorAction SilentlyContinue) {
    Start-Process taskkill -Verb RunAs -Wait -ArgumentList '/F /IM VibeMate.exe'
    Start-Sleep -Seconds 3
}

# 3) 发布（随行脚本由 csproj 的 CopyToOutputDirectory 自动带上；pdb 源头就不产出）
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
# PATH 里的 dotnet 可能只装了运行时没有 SDK —— 必须验真，否则 publish 会谜之失败
if ($dotnet -and -not (& $dotnet --list-sdks 2>$null)) { $dotnet = $null }
if (-not $dotnet -and (Test-Path 'D:\dotnetsdk\dotnet.exe')) { $dotnet = 'D:\dotnetsdk\dotnet.exe' }   # 本机开发环境兜底
if (-not $dotnet) { Write-Host 'PUBLISH FAILED: dotnet SDK not found in PATH'; exit 1 }
& $dotnet publish $proj -c Release -r win-x64 --self-contained -o $rel
if ($LASTEXITCODE -ne 0) { Write-Host 'PUBLISH FAILED'; exit 1 }

# 4) 起服务 + 冒烟（刚起的监听器可能晚一两秒就绪 —— 重试 10s）
schtasks /Run /TN $tn | Out-Null
$smoke = $null
for ($i = 0; $i -lt 10; $i++) {
    Start-Sleep -Seconds 1
    try { $smoke = Invoke-RestMethod "http://127.0.0.1:8787/api/ping"; break } catch {}
}
if ($smoke) { Write-Host "SMOKE OK: version=$($smoke.version)" }
else { Write-Host "SMOKE FAILED （看 $rel\vibe.log）" }
Write-Host "DEPLOYED via scheduled task $tn (logon-autostart enabled)"
