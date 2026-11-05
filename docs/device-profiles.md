# 设备配置档案与社区共享（设计稿）

> 状态：**设计稿，未实现**（2026-11-05）。本文档描述目标行为与实现拆解，
> 代码实现留待后续版本。现有能力见 README「支持设备」。
>
> 关联：`devices.json`、`VibeMate/DeviceDb.cs`、按键学习（`KeysRole`）、
> 配置热更新（`ConfigService`）、`tap/tap.c` 的 GATT 桥接。

## 1. 背景与目标

现状：

- `devices.json`：内置 + 云端刷新，按 `vid + pid + names(+ revs)` 匹配，预填型号、
  语音能力与键名标签。
- 学习模式：按「连按 3 次」推导报告指纹，学完保存到本机 `config.devices[地址]`
  （按蓝牙地址，不跨设备、不可分享）。
- 推送型设备（无 `kbdhid`/`mouhid` 读者，如 Google rev 0110）：自动走 GATT 桥接；
  未知厂商码以 `0xFF00 | 码` 伪 usage 呈现，可被学习。

目标（用户提出的四条）：

1. 配置随程序下发，也支持从云端获取；
2. 同一设备自动调取对应配置并导入；
3. 新设备作为新条目录入、保存；
4. 用户可在 GitHub 分享自己设备的配置，其他人下载后直接可用。

补充约束（2026-11-05 确认）：

- **常见设备档案随安装包打包**（宁可包大一点），下载即用、离线可用；
- **包内没有的新设备，默认先联网查**，查到询问下载，查不到再引导学习；
- 包体积增长可接受（单个档案约 1–2KB，几十个设备也只有几十 KB，
  相对 ~60MB 的单文件 exe 可忽略）。

核心转变：**默认不假设任何设备是已知的**。所有设备先按「身份」查找配置；
找不到就引导学习并生成新的可分享档案。

## 2. 概念模型

| 概念 | 定义 |
|---|---|
| 设备身份 `DeviceId` | `vid:pid:rev`（小写十六进制），如 `18d1:9450:0110`；rev 缺失时用 `vid:pid:*` |
| 报告指纹 `report` | `{ len, id, off, w }`：报告长度、ReportID、usage 槽偏移/宽度 |
| 档案 `Profile` | 一份可移植的设备配置：身份 + 名称 + 语音能力 + 指纹 + 键名标签（+ 可选动作预设 + 来源元数据） |
| 内置表 | 随 exe 嵌入的 `devices.json`（现有兼容层：型号/语音/标签） |
| 打包档案 | 随安装包一起发布的 `profiles/*.json`（常见设备，离线可用） |
| 本机档案 | 用户学习/导入产生的档案，保存在本机 `profiles/` 目录 |
| 云端档案 | 社区在 GitHub 维护的 `profiles/` 目录与索引 |

匹配优先级（高 → 低）：

```
本机档案（按 DeviceId） > 打包档案 > 云端缓存 > 内置 devices.json
```

查找顺序（与上述优先级一致）：

1. 本机学到的档案；
2. **安装包内置的常见设备档案**（不联网，直接套用）；
3. 以上都没有 → 才去云端查（新设备的主路径）；
4. 云端也没有 → 提示手动学习。

地址级旧配置 `config.devices[addr]` 仍然保留：它是「这台遥控器当前用户的选择」，
优先级高于以上预填逻辑；新流程只负责在**没有用户配置时**提供默认值。

## 3. 用户流程（目标行为）

```
用户选中遥控器 / 首次连接
        │
        ▼
计算 DeviceId ──► 本机档案 / 打包档案 有？
        │ 有                         │ 无
        ▼                            ▼
 直接套用（不打扰，离线可用）  联网查询云端索引（GitHub CDN，超时 5s，失败静默）
                                     │
                    ┌────────────────┴────────────────┐
                    ▼                                 ▼
              云端有档案                          云端无档案
                    │                                 │
       弹窗询问「找到此设备的社区配置             提示「云端暂无该设备配置，
       （作者/更新时间/键位数量），              请使用学习模式手动录入」
       是否下载并应用？」                        + 学习入口按钮
          │            │
         是            否
          │            └─► 记住选择（本次会话不再问 / 永久忽略，可选）
          ▼
  下载 → 校验 schema → 写入本机档案 → 预填标签
  （动作预设需用户再次确认后应用）
```

学习完成并保存后：

- 写入地址级配置（现有行为，`config.devices[addr]`）；
- **同时**生成可移植档案（按 DeviceId），提供「导出」与「提交到社区」入口；
- 导出文件可直接作为 GitHub PR 的附件/文件。

## 4. 本地存储布局

```
安装目录（打包档案，随安装包发布）
  profiles\
    index.json                   # 打包档案索引（也可直接嵌入 exe 资源）
    google-9450-0110.json
    google-9450-011b.json
    xiaomi-2717-32b8-00a4.json

%ProgramData%\VibeMate\
  profiles\
    18d1-9450-0110.json          # 本机档案（学习/导入产生）
    index.json                   # 可选：本机档案索引（启动时快速加载）
  profiles-cache\
    index.json                   # 云端索引缓存
    <sha256>.json                # 云端档案缓存（按内容哈希去重）
```

- 打包档案两种放法二选一或并用：**嵌入 exe 资源**（与 `devices.json` 同机制，
  用户删不掉）或**随 release zip 释放**到安装目录；建议嵌入 + 首次运行释放一份
  可读副本，便于排障。
- 文件名与档案内 `device.vid/pid/rev` 必须一致（导入时校验，防改名冲突）。
- 同一 DeviceId 出现多份档案时：按 `report` 指纹匹配当前设备；仍冲突则让用户选择。
- 删除/覆盖前备份到 `profiles\.backup\`（最多保留 3 份）。
- 读取失败（坏 JSON）不影响启动：记录日志并跳过。

## 5. 档案格式（Schema v1，草案）

```json
{
  "schemaVersion": 1,
  "device": {
    "vid": "18d1",
    "pid": "9450",
    "revs": ["0110"],
    "names": ["chromecast remote", "chromecast voice remote"]
  },
  "voice": "atvv",
  "report": { "len": 3, "id": 2, "off": 1, "w": 2 },
  "labels": {
    "0x0042": "up",
    "0x0043": "down",
    "0xff03": "custom-03"
  },
  "actions": {
    "up": { "type": "key", "value": "UP", "mods": [], "text": "", "usage": "0x0042" }
  },
  "meta": {
    "author": "github-user",
    "updated": "2026-11-05T12:00:00Z",
    "source": "learned",
    "verified": true,
    "note": "rev 0110, Windows 10 19045"
  }
}
```

说明：

- `labels`：usage → 键名（现有 `devices.json` 的 `keys` 字段同义）。
- `actions`：**可选**动作预设（动作名 → 映射），默认导出不含它；
  用户勾选后才随档案分享。导入时也必须二次确认（避免覆盖已有映射）。
- `0xFFxx` 是推送型设备未知厂商码的保留伪 usage：`0xFF00 | 厂商码`。
  分享档案中必须保持原样，保证不同用户机器上行为一致。
- 校验：字段白名单；字符串长度上限；usage 必须在 `0x0000–0xFFFF`；
  `report.len ∈ [1,64]`、`w ∈ {1,2}`、`off + w ≤ len`；文件 ≤ 64KB。

## 6. 云端仓库布局与查询

沿用现有云端仓库（`mumutoy/vibe-mote`，`devices.json` 所在仓库）新增目录：

```
devices.json                 # 现有总表（保留：型号/语音/默认标签）
profiles/
  index.json                 # 设备 ID → 档案文件 + 元数据 + sha256
  google-9450-0110.json
  xiaomi-2717-32b8-00a4.json
```

`index.json` 草案：

```json
{
  "schemaVersion": 1,
  "updated": "2026-11-05T12:00:00Z",
  "devices": {
    "18d1:9450:0110": {
      "file": "google-9450-0110.json",
      "sha256": "…",
      "author": "github-user",
      "updated": "2026-11-05T12:00:00Z",
      "buttons": 14
    }
  }
}
```

查询策略（与 `DeviceDb.RefreshAsync` 一致的多源轮询）：

1. 若本机档案命中 → 不查云端；
2. 读 `profiles-cache/index.json`（如有）先判本地缓存；
3. jsdelivr CDN → raw.githubusercontent.com，超时 5s，失败静默；
4. 命中后按索引里的 sha256 下载并校验。

缓存策略：索引与档案都缓存到 `profiles-cache\`；索引带 `updated`，本地比对后
再决定是否拉取，避免每次启动都下载。后台刷新不阻塞启动。

## 7. 隐私与安全

- **默认只分享**：设备身份、指纹、标签、元数据；**不含**蓝牙地址、用户名、
  完整配置（映射/宏/文本动作）。
- 动作预设属于可选分享内容，导出界面必须提供勾选与预览。
- 导入展示 diff：将新增/覆盖哪些键名与映射，用户确认后写入。
- 所有云端内容按不可信输入处理：schema 校验、大小限制、sha256、禁止任何
  可执行内容；不自动应用动作预设（必须确认）。
- 学习产物和导入档案都记录来源（`meta.source`：`learned` / `imported` / `cloud`）。

## 8. GitHub 分享流程（贡献者）

1. 应用内导出档案 JSON（文件名建议 `<vid>-<pid>-<rev>.json`）；
2. Fork 云端仓库，放到 `profiles/`，更新 `profiles/index.json`；
3. 提 PR（模板：设备型号、Windows 版本、rev、已测试按键）；
4. CI 校验：JSON schema、文件名与 DeviceId 一致、sha256 与索引匹配、大小限制；
5. 合并后所有用户在下一次查询时可见。

（可选）Issue 模板「请求支持新设备」，附导出文件。

## 9. 内部 API（实现参考）

| 方法 | 路径 | 用途 |
|---|---|---|
| GET | `/api/device-profile?addr=<地址>` | 返回当前选中设备：DeviceId、匹配来源（内置/本机/云端）、是否已有用户配置 |
| POST | `/api/device-profile/lookup` | 云端查询：`{ addr }` → `{ found, meta }`（结果缓存） |
| POST | `/api/device-profile/import` | 导入档案（文件内容或本机路径） |
| GET | `/api/device-profile/export?addr=<地址>` | 导出当前设备的可移植档案（下载） |
| POST | `/api/device-profile/apply` | 应用档案（可选择是否应用动作预设） |

现有 `GET /api/btdevices` 可扩展返回每个设备的 DeviceId 与档案来源标记，
供设置页显示「内置 / 本机 / 社区」徽标。

## 10. 实现拆解

### P1 · 本机档案（可移植、可导入导出、随包发布）

- 新增 `DeviceProfile` 模型（序列化/校验）；
- 档案存储：`ProgramData\VibeMate\profiles\`；
- **打包档案**：仓库新增 `profiles/` 目录，csproj 以 `EmbeddedResource` 收进 exe
  （与 `devices.json` 同机制），发布时同时复制到 release 目录；
  首批收录已知常见设备（Google 0110/011b、小米 RC003 等）；
- 学习保存时双写（地址级配置 + 可移植档案）；
- `GET /export`、`POST /import`、`POST /apply`；
- 设置页：导出/导入按钮、档案来源徽标（本机 / 打包 / 社区）；
- 触摸文件：`DeviceDb.cs`、`ConfigService.cs`、`KeysRole.cs`、`HttpServer.cs`、
  `web/`、`VibeMate.csproj`、`profiles/`、`README.md`。

### P2 · 云端查询与分享闭环

- 本地（本机 + 打包）未命中时才查询云端——**新设备主路径**；
- `profiles/index.json` 拉取与缓存（多源轮询，复用 DeviceDb 的套路）；
- 前端弹窗（有则询问下载，无则提示学习）；
- 下载校验（schema + sha256）；
- 仓库 `profiles/` 目录、CI 校验、PR/Issue 模板；
- 触摸文件：`DeviceDb.cs`（或新增 `ProfileStore.cs`）、`HttpServer.cs`、`web/`、
  仓库根 `profiles/`、`.github/`。

### P3 · 默认未知化（较大重构）

- 首次连接统一走探测：HID 读者 / GATT 回调 / ATVV 能力；
- `config.keys` 从全局改为「按 DeviceId 的映射集」，切设备自动切换；
- 未知设备引导式快速学习（每键连按 3 次）；
- 触摸文件：`ConfigService.cs`（配置结构迁移）、`KeysRole.cs`、`Program.cs`、`web/`。

> 注意：P3 会改变配置结构，需要迁移脚本与向后兼容（旧配置视为默认装备份）。

## 11. 开放问题

1. **动作预设是否纳入分享**：隐私 vs 开箱即用。建议默认不含、可选勾选。
2. **同型号多档案**（不同 rev/批次键位不同）：按 rev 分文件；缺 rev 的档案标注
   「通用」，匹配时降低优先级并在 UI 上提示。
3. **0xFFxx 伪 usage 的跨设备稳定性**：取决于厂商码字节是否稳定（同一固件通常稳定）；
   若某厂商用位置型报告（非码值），需要 GATT 策略扩展（P3 之后再评估）。
4. **离线用户**：无网络时只使用内置/本机档案；查询失败不打扰。
5. **多设备同时连接**：仍以 `remote_addr` 选中项为准；查询只针对选中设备。

## 12. 验收清单（P1+P2）

- [ ] 打包档案覆盖的常见设备：**断网**首次连接即可用（不提示下载）。
- [ ] 未知设备首次连接：先查云端；有档案则弹窗询问，拒绝后本次不再打扰。
- [ ] 云端无档案时：提示文案与学习入口正确。
- [ ] 未知设备离线：提示可学习，不影响按键/语音其他功能。
- [ ] 同 DeviceId 的另一台机器导入档案后：标签自动预填，按键可用，无需再学。
- [ ] 学完可导出档案；导出文件不包含蓝牙地址与用户自定义动作（除非显式勾选）。
- [ ] 导入恶意/超大/坏 JSON：被拒绝且程序不受影响。
- [ ] 断网/超时：启动、按键、语音均不受影响。
- [ ] 发布包内含 `profiles/` 且 exe 内嵌同版本副本（sha 一致）。
