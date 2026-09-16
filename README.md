# VibeMate

[![build](https://github.com/fluctio-ai/vibemate/actions/workflows/build.yml/badge.svg)](https://github.com/fluctio-ai/vibemate/actions/workflows/build.yml)

把 Google TV / Chromecast 蓝牙遥控器变成 Windows 的「**按住说话 + 快捷键面板**」。

开源地址：https://github.com/fluctio-ai/vibemate

纯用户态单文件程序：遥控器的语音键按住说话（经虚拟声卡发给输入法），
其余 14 个按键可映射为键盘 / 鼠标 / 组合键 / 文本命令，网页控制台配置一切。

```
遥控器 ══BLE GATT══> VibeMate.exe ══虚拟声卡══> 输入法（语音）
                       ║
                       ╚══SendInput══> 任意应用（按键映射）
```

## 功能

- **语音输入**：按住遥控器语音键 → 合成 `Win+Ctrl` → 输入法从 VB-CABLE 收音。
  音频链路纯 C# 实现：IMA ADPCM 解码 → 去直流 → 高通 → 重采样 → AGC → 软限幅。
- **按键映射**：14 键任意映射（单键/组合/按住/鼠标/滚轮/文本/长按双动作），
  网页「学习模式」按遥控器键即可配置，即时生效。
- **托盘动效**：待机呼吸 / 按键快闪 / 语音声浪三态动画（3×3 宫格素材）。
- **单文件分发**：网页、注入 DLL、VB-CABLE 驱动、安装脚本全部嵌在一个 exe 里；
  首启自动释放「一键安装.bat」，自动静默安装虚拟声卡。
- **安全**：HTTP 只监听 127.0.0.1；POST 双闸（Origin 白名单 + 强制 JSON）。

## 下载

到 [Actions](https://github.com/fluctio-ai/vibemate/actions) 页（build 工作流）下载最新的
`VibeMate-win-x64` 产物，解压即完整发布包（单文件 exe + 一键安装脚本）。

## 快速开始（用户）

1. Windows 蓝牙设置配对遥控器（Google TV / Chromecast Remote）
2. 双击 `一键安装.bat`（弹一次 UAC）——建最高权限登录任务并启动
3. 左键点托盘图标打开 `http://127.0.0.1:8787`，设置页选择遥控器
4. 输入法麦克风选「CABLE Output」，按住遥控器语音键说话

详见程序内「安装帮助」页。

## 构建（开发者）

```powershell
# 主程序（.NET 10 SDK，win-x64 自包含单文件，~60MB）。
# 发布管线（单文件/压缩/无 pdb）收敛在 csproj 的 Release 组，命令只传 -c Release；
# 随行安装脚本自动进输出目录 —— release\ 即完整发布包
dotnet publish VibeMate -c Release -r win-x64 --self-contained -o release
```

注入 DLL（`tap/`，一般不需要重新编译——预编译产物已入库）：

```bat
cd tap && build.bat      # 需要 zig（zig cc -target x86_64-windows-gnu）
```

push 到 main 即触发 GitHub Actions 自动编译（`.github/workflows/build.yml`），
产物挂 Actions Artifact。

## 架构速览

| 文件 | 职责 |
|---|---|
| `Program.cs` | 入口 + 装配：单实例锁、首启提权、托盘/角色接线、随行文件释放 |
| `VoiceRole.cs` | WinRT BLE GATT → ATVV 私有服务 → 音频通知 → 管线 |
| `AudioPipeline.cs` / `AudioSink.cs` / `MicRelay.cs` | 音频五级管线 / CABLE 出口看护 / 键盘触发直通 |
| `KeysRole.cs` | tap.dll 宿主侧：定位 WUDFHost → 注入 → 管道协议 → 映射执行 |
| `KeysInput.cs` | SendInput 合成（扩展键标志、扫描码等 v1 实测坑全保留） |
| `TrayIcon.cs` | 托盘 + 三态动效（宫格素材切帧，HICON 缓存） |
| `HttpServer.cs` | 127.0.0.1 静态页 + JSON API + 计划任务管理 |
| `ConfigService.cs` | config.json 唯一读写者（原子替换 + 热推送） |
| `CableSetup.cs` | VB-CABLE 静默安装（解压 → pnputil → SwDeviceCreate） |
| `web/index.html` | 单文件离线控制台（状态/映射/语音/设置/帮助/日志） |
| `tap/tap.c` | 注入 DLL：钩 WUDFHost 的 HID IOCTL，屏蔽+上报（MinHook） |

## 第三方组件

- **VB-CABLE**（`third-party/vbcable/`）：VB-AUDIO Software 出品的虚拟声卡驱动，
  版权归 VB-AUDIO 所有（[vb-audio.com](https://vb-audio.com/Cable/)）。
  本仓库仅内嵌其官方安装包文件用于自动安装，请支持原作者。
- **MinHook**（`tap/minhook/`）：API hooking 库，其原始许可证适用。
- **NAudio**：音频 I/O（NuGet）。

## 致谢

本项目基于 [Tilkmilk/vibe-mote](https://github.com/Tilkmilk/vibe-mote) 发展而来
（其 Python 版为 v1，本项目是同一思路的 C# 重写），感谢原作者的设计与实现。

## 免责声明

本项目通过未公开的 IOCTL 与 API 注入实现按键映射，仅供个人学习与研究。
Google、Chromecast、VB-AUDIO 等商标归各自所有者。使用风险自负。
