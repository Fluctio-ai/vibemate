# VibeMate

[![build](https://github.com/fluctio-ai/vibemate/actions/workflows/build.yml/badge.svg)](https://github.com/fluctio-ai/vibemate/actions/workflows/build.yml)

把 Google TV / Chromecast 蓝牙遥控器变成 Windows 的「**按住说话 + 快捷键面板**」。

开源地址：https://github.com/fluctio-ai/vibemate

纯用户态单文件程序：遥控器的语音键按住说话（经虚拟声卡发给输入法），
其余 14 个按键可映射为键盘 / 鼠标 / 组合键 / 文本命令 / 多步宏，网页控制台配置一切。

```
遥控器 ══BLE GATT══> VibeMate.exe ══虚拟声卡══> 输入法（语音）
                       ║
                       ╚══SendInput══> 任意应用（按键映射）
```

## 功能

- **语音输入**：按住遥控器语音键 → 合成 `Win+Ctrl` → 输入法从 VB-CABLE 收音。
  音频链路纯 C# 实现：IMA ADPCM 解码 → 去直流 → 高通 → 重采样 → AGC → 软限幅。
- **按键映射**：14 键任意映射（单键/组合/按住/鼠标/滚轮/文本），每个键还支持
  **长按**（≥0.4s）和**双击**（快速按两下）各触发另一套动作，以及**宏**——
  一次按键按序执行多步（例：`Ctrl+A` 全选 → `Delete` 删除 → 输入 `abc`，
  步间隔 50ms）。宏在主映射/长按/双击三处均可配置。
  网页「学习模式」按遥控器键即可配置，即时生效。
- **未知遥控器**：不在设备库的遥控器走「设备学习」（每键连按 3 次确认 +
  报文指纹自动推导），已知型号（Google rev 0110/011b、小米 RC003 等，
  含硬件版本 rev 区分）免学习直接用。较新的 **0110** 批次按键不走 Windows
  HID 读路径，由 VibeMate 在驱动宿主内挂钩 GATT 通知回调取键；其他推送型
  BLE 遥控器会被自动识别并同样走回调 + 学习（见「支持设备」）。
- **托盘动效**：待机呼吸 / 按键快闪 / 语音声浪三态动画（3×3 宫格素材）。
- **单文件分发**：网页、注入 DLL、VB-CABLE 驱动、安装脚本全部嵌在一个 exe 里；
  首启自动释放「一键安装.bat」，自动静默安装虚拟声卡。
- **声卡管家**：虚拟声卡自动静默安装，语音页可一键卸载（之后不再自动装回）。
- **更新提示**：后台检查 GitHub Releases，有新版本时状态页直接给下载链接。
- **安全**：HTTP 只监听 127.0.0.1；POST 双闸（Origin 白名单 + 强制 JSON）。

## 支持设备

| 设备 | 硬件 rev | 按键 | 语音 |
|---|---|---|---|
| Google Chromecast Remote / Voice Remote | **0110**（较新批次） | GATT 回调桥接（驱动级，免学习） | ATVV |
| Google Chromecast Voice Remote | **011b** | 标准 HID 读报文 | ATVV |
| 小米蓝牙遥控器 2 Pro（RC003） | 00a4 | 设备学习（预填部分键名） | ATVV |
| 其他 BLE HID 遥控器 | — | 标准 HID 走学习模式；**推送型**（无键盘/鼠标读者）自动走 GATT 桥接 + 学习 | 运行时探测（带 ATVV 才有） |

- rev 取自 Windows 蓝牙设备节点名（`BTHLEDevice\..._REV&xxxx_...`），设置页
  设备列表可直接看到；同型号不同批次靠它区分。
- **推送型设备自动识别**：程序检查该设备 HID 集合是否挂了 `kbdhid`/`mouhid`
  读者；没有（如 0110 批次）就自动启用 GATT 桥接，不再依赖型号白名单。
  找不到 HID 节点时退回 rev 白名单。
- **0110 说明**：该批次遥控器只上报一个消费控制集合、系统侧没有键盘/鼠标
  驱动读取它，因此按键报告不经过常规 HID 读接口。VibeMate 注入驱动宿主后
  按字节签名定位 `HogpGattAdapter::OnReportValueChanged` 并桥接按键，再把
  厂商码归一为现有映射使用的 usage（上/下/左/右/OK/返回/主页/音量/静音/
  电源/输入/YouTube/Netflix 全部支持）。签名未命中时会记录
  `[gatt] signature not found (unsupported build)`，此时按键不可用（语音不受影响）。
- **未知厂商的推送型设备**：按键码在学习模式里以 `0xFFxx` 伪 usage 呈现
  （`0xFF00 | 厂商码`），可像普通键一样逐键学习并绑定动作，无需预先收录。
- **设备配置档案（v2.3.0）**：常见设备档案随安装包内置（离线开箱即用，设置页
  设备列表有「📘内置档案」标记）；本机学习成果自动生成可移植档案（「📗本机档案」），
  设置页可导出/导入；未收录的新设备自动联网查询社区配置（GitHub
  [主仓库](https://github.com/Fluctio-ai/vibemate) `profiles/` 目录），查到询问
  下载，查不到引导学习。档案只含设备身份 + 报告指纹 + 键名（不含蓝牙地址与
  个人映射，动作预设默认不分享）。详见
  [docs/device-profiles.md](docs/device-profiles.md)。

## 下载

正式发布到 [Releases](https://github.com/Fluctio-ai/vibemate/releases)（推荐，
公开直链无需登录）；最新版直达
[releases/latest](https://github.com/Fluctio-ai/vibemate/releases/latest)，
下载 zip 解压即完整发布包（单文件 exe + 一键安装脚本）。

开发快照在 [Actions](https://github.com/Fluctio-ai/vibemate/actions) 页（build
工作流的 Artifact，需登录 GitHub 且 90 天过期）。

发版流程：`git tag v2.1.x && git push origin v2.1.x` → CI 自动建 Release 附 zip。

## 快速开始（用户）

1. Windows 蓝牙设置配对遥控器（Google TV / Chromecast Remote）
2. 双击 `一键安装.bat`（弹一次 UAC）——建最高权限登录任务并启动
3. 左键双击托盘图标打开 `http://127.0.0.1:8787`，设置页选择遥控器
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
| `CableUninstall.cs` | VB-CABLE 静默卸载（服务键/注册表/驱动包白名单清理） |
| `UpdateCheck.cs` | 版本检查后台循环（GitHub Releases → /api/state） |
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
