using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace VibeMate;

/// <summary>
/// 设备档案库（docs/device-profiles.md，P1+P2）—— 「适配各种不同设备」的核心：
/// 一份可移植的设备配置（身份 + 报告指纹 + 键名标签，可选动作预设），随安装包
/// 打包、本机学习产生、或从云端社区下载。同一型号的另一台机器导入后开箱即用，
/// 无需再学。
///
/// 匹配优先级（高→低）：本机档案（ProgramData）&gt; 打包档案（exe 内嵌资源）
/// &gt; 云端缓存 —— 本地全 Missed 才引导查云端（新设备主路径）。
/// 云端多源轮询对齐 DeviceDb：jsdelivr → raw.githubusercontent，超时 5s，
/// 失败静默（档案是便利层不是依赖，断网全功能）。
///
/// 与 DeviceDb 的分工：DeviceDb 是「型号/语音预填的兼容层」（按 vid+pid+蓝牙名
/// 认定，devices.json）；ProfileStore 是完整可分享档案（按 DeviceId 认定，含
/// 0xFFxx 伪 usage 的学习成果）。首批打包档案从 devices.json 生成，双轨并存，
/// P3（默认未知化）再收敛为档案单源。
/// </summary>
internal static class ProfileStore
{
    private static readonly string DataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VibeMate");
    private static readonly string LocalDir = Path.Combine(DataRoot, "profiles");
    private static readonly string BackupDir = Path.Combine(LocalDir, ".backup");
    private static readonly string CloudDir = Path.Combine(DataRoot, "profiles-cache");
    private static readonly string IndexPath = Path.Combine(CloudDir, "index.json");

    // 云端 profiles/ 随 devices.json 走同一个仓库（Fluctio-ai/vibemate）；改仓库只动这里
    private static readonly string[] IndexUrls =
    {
        "https://cdn.jsdelivr.net/gh/Fluctio-ai/vibemate@main/profiles/index.json",
        "https://raw.githubusercontent.com/Fluctio-ai/vibemate/main/profiles/index.json",
    };
    private static string[] FileUrls(string file) => new[]
    {
        $"https://cdn.jsdelivr.net/gh/Fluctio-ai/vibemate@main/profiles/{file}",
        $"https://raw.githubusercontent.com/Fluctio-ai/vibemate/main/profiles/{file}",
    };

    static ProfileStore() => LoadIndexCache();

    // ================= 身份 =================

    /// <summary>hex 规整：去 0x 前缀 → 小写（vid/pid/rev 共用；null 安全）。</summary>
    private static string Norm(string? s) =>
        (s ?? "").Trim().StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? s!.Trim()[2..].ToLowerInvariant()
            : (s ?? "").Trim().ToLowerInvariant();

    /// <summary>4 位十六进制校验（vid/pid/rev 共用）。</summary>
    private static bool Hex4(string s) => s.Length == 4 && s.All("0123456789abcdef".Contains);

    /// <summary>设备身份：vid:pid:rev（rev 缺失用 *，如 18d1:9450:0110 / 2717:32b8:*）。</summary>
    internal static string DeviceIdOf(string? vid, string? pid, string? rev) =>
        $"{Norm(vid)}:{Norm(pid)}:{(string.IsNullOrWhiteSpace(rev) ? "*" : Norm(rev))}";

    /// <summary>vid:pid:*（云端索引的通用档案键 —— 档案不带 revs 时按此收录）。</summary>
    private static string GenericId(string deviceId) =>
        deviceId.Substring(0, deviceId.LastIndexOf(':') + 1) + "*";

    /// <summary>addr（任意格式）→ 设备身份（vid/pid/rev + 蓝牙名）。注册表读不到
    /// 返回 null（BLE 断开重连间隙可能短暂读不到 —— 调用方跳过档案逻辑即可）。
    /// HttpServer 档案路由与 KeysRole 指纹回退共用：Mac12 解析也是身份知识，收在
    /// 这里，调用方不再各自拼 Mac12 + IdentityOfMac。</summary>
    internal static (string Vid, string Pid, string Rev, string Name)? IdentityOfAddr(string? addr)
    {
        var mac12 = KeysRole.Mac12(addr ?? "");
        return mac12.Length != 12 ? null : IdentityOfMac(mac12);
    }

    /// <summary>按 MAC 读设备身份（vid/pid/rev + 蓝牙名）。注册表读不到返回 null
    /// （BLE 设备断开重连间隙可能短暂读不到 —— 调用方跳过档案逻辑即可，不影响按键）。</summary>
    internal static (string Vid, string Pid, string Rev, string Name)? IdentityOfMac(string mac12)
    {
        if (mac12.Length != 12) return null;
        var vp = KeysRole.VidPidOfMac(mac12);
        if (vp is not { } v) return null;
        return (v.Vid, v.Pid, KeysRole.RevOfMac(mac12), BtNameOf(mac12) ?? "");
    }

    /// <summary>蓝牙栈的设备名表（BTHPORT\Parameters\Devices\MAC 的 Name，实测
    /// REG_BINARY：ASCII/UTF-8 字节 + 尾部 \0）。与 HttpServer.ListPairedBtDevices
    /// 同源知识，导出档案时档案里 names 字段用。</summary>
    internal static string? BtNameOf(string mac12)
    {
        try
        {
            using var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices");
            using var k = root?.OpenSubKey(mac12);
            var n = k?.GetValue("Name");
            var s = n switch
            {
                string str => str,
                byte[] bytes => Encoding.UTF8.GetString(bytes).TrimEnd('\0'),
                _ => "",
            };
            return s.Length > 0 ? s : null;
        }
        catch { return null; }
    }

    // ================= 解析与校验（所有来源共用的唯一闸口）=================

    /// <summary>usage 字符串 → 数值（"0x0042"/"42" 均可）。不合法返回 null。</summary>
    private static ushort? UsageOf(string? s)
    {
        s = (s ?? "").Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return ushort.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var u)
            && u != 0 ? u : null;
    }

    private static int? IntOf(JsonNode? n)
    { try { return n?.GetValue<int>(); } catch { return null; } }

    /// <summary>安全取字符串：非字符串/超长 → ""（调用方把空值当非法处理）。</summary>
    private static string StrOf(JsonNode? n, int maxLen)
    { try { var s = n?.GetValue<string>() ?? ""; return s.Length <= maxLen ? s : ""; } catch { return ""; } }

    /// <summary>档案解析 + schema 校验（本机/打包/导入/云端下载共用 —— 全部按
    /// 不可信输入处理：字段白名单、长度上限、usage 范围、report 边界、64KB 上限；
    /// 校验通过后重建只含已知字段的干净对象，陌生键一概丢弃）。
    /// 返回 (档案, 错误)；错误非空 = 拒收。</summary>
    internal static (JsonObject? P, string Err) Parse(string text)
    {
        if (text.Length > 64 * 1024) return (null, "档案超过 64KB 上限");
        JsonObject o;
        try { if (JsonNode.Parse(text) is JsonObject j) o = j; else return (null, "根节点必须是对象"); }
        catch (Exception e) { return (null, "JSON 解析失败：" + e.Message); }
        if (IntOf(o["schemaVersion"]) != 1) return (null, "schemaVersion 必须是 1");

        // device（必填）
        if (o["device"] is not JsonObject dev) return (null, "缺少 device 字段");
        var vid = Norm(StrOf(dev["vid"], 8));
        var pid = Norm(StrOf(dev["pid"], 8));
        if (!Hex4(vid) || !Hex4(pid))
            return (null, "device.vid/pid 必须是 4 位十六进制");
        var revs = new JsonArray();
        foreach (var r in dev["revs"] as JsonArray ?? new JsonArray())
        {
            var rv = Norm(StrOf(r, 8));
            if (rv.Length == 0) continue;
            if (!Hex4(rv))
                return (null, "device.revs 必须是 4 位十六进制");
            revs.Add(rv);
            if (revs.Count > 8) return (null, "device.revs 最多 8 项");
        }
        var names = new JsonArray();
        foreach (var n in dev["names"] as JsonArray ?? new JsonArray())
        {
            var s = StrOf(n, 64);
            if (s.Length == 0) return (null, "device.names 不能有空项");
            names.Add(s);
            if (names.Count > 8) return (null, "device.names 最多 8 项");
        }

        // report（必填 —— 没有指纹的档案没有应用价值）
        if (o["report"] is not JsonObject rep) return (null, "缺少 report 字段");
        var (len, id, off, w) = (IntOf(rep["len"]), IntOf(rep["id"]), IntOf(rep["off"]), IntOf(rep["w"]));
        if (len is null or < 1 or > 64 || id is null or < 0 or > 255
            || off is null or < 0 || w is not (1 or 2) || off + w > len)
            return (null, "report 字段非法（len∈[1,64]、id∈[0,255]、w∈{1,2}、off+w≤len）");

        // labels（必填，可为空对象 —— 学习第一个键保存前 report 已知、labels 未有）
        if (o["labels"] is not JsonObject labelsIn) return (null, "缺少 labels 字段");
        var labels = new JsonObject();
        foreach (var (k, v) in labelsIn)
        {
            if (UsageOf(k) is not { } u) return (null, $"labels 键 {k} 不是合法 usage");
            var name = StrOf(v, 30);
            if (name.Length == 0) return (null, "labels 值不能为空");
            labels[$"0x{u:X4}"] = name;
            if (labels.Count > 64) return (null, "labels 最多 64 项");
        }

        // actions（可选；type 白名单与 Mapper.Execute 支持集一致，越界类型执行期
        // 本就直接忽略，这里先挡掉省得垃圾入库）
        JsonObject? actions = null;
        if (o["actions"] is JsonObject actsIn)
        {
            actions = new JsonObject();
            foreach (var (name, a) in actsIn)
            {
                if (a is not JsonObject a2) return (null, $"actions.{name} 必须是对象");
                var type = StrOf(a2["type"], 16);
                if (type is not ("key" or "combo" or "seq" or "hold" or "holdcombo"
                                 or "mouse" or "holdmouse" or "text"))
                    return (null, $"actions.{name}.type 不受支持");
                var usage = UsageOf(StrOf(a2["usage"], 12));
                if (usage is null)
                    return (null, $"actions.{name}.usage 缺失或不合法");
                var clean = new JsonObject
                {
                    ["type"] = type, ["value"] = StrOf(a2["value"], 64),
                    ["text"] = StrOf(a2["text"], 200),
                    ["usage"] = $"0x{usage:X4}".ToLowerInvariant(),
                };
                var mods = new JsonArray();
                foreach (var m in a2["mods"] as JsonArray ?? new JsonArray())
                {
                    var ms = StrOf(m, 16);
                    if (ms.Length == 0) return (null, $"actions.{name}.mods 不能有空项");
                    mods.Add(ms);
                }
                clean["mods"] = mods;
                if (a2["steps"] is JsonArray st)
                {
                    var steps = new JsonArray();
                    foreach (var s in st.OfType<JsonObject>())
                    {
                        var stype = StrOf(s["type"], 16);
                        if (stype is not ("key" or "combo" or "mouse" or "text"))
                            return (null, $"actions.{name}.steps 含不支持的类型");
                        steps.Add(new JsonObject
                        {
                            ["type"] = stype,
                            ["value"] = StrOf(s["value"], 64),
                            ["text"] = StrOf(s["text"], 200),
                            ["mods"] = new JsonArray(),
                        });
                    }
                    clean["steps"] = steps;
                }
                if (a2["long"] is JsonObject lo) { lo = (JsonObject)lo.DeepClone(); StripAction(lo); clean["long"] = lo; }
                if (a2["double"] is JsonObject db) { db = (JsonObject)db.DeepClone(); StripAction(db); clean["double"] = db; }
                actions[name.Length > 0 && name.Length <= 30 ? name : "key"] = clean;
                if (actions.Count > 64) return (null, "actions 最多 64 项");
            }
        }

        // meta（可选）
        JsonObject? meta = null;
        if (o["meta"] is JsonObject mIn)
        {
            var src = StrOf(mIn["source"], 16);
            meta = new JsonObject
            {
                ["author"] = StrOf(mIn["author"], 64),
                ["updated"] = StrOf(mIn["updated"], 40),
                // source 白名单只作标签用途，越界值丢弃归空
                ["source"] = src is ("learned" or "imported" or "cloud" or "builtin") ? src : "",
                ["note"] = StrOf(mIn["note"], 200),
            };
            if (mIn["verified"] is { } vf) { try { meta["verified"] = vf.GetValue<bool>(); } catch { } }
        }

        var p = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["device"] = new JsonObject { ["vid"] = vid, ["pid"] = pid, ["revs"] = revs, ["names"] = names },
            ["report"] = new JsonObject { ["len"] = len, ["id"] = id, ["off"] = off, ["w"] = w },
            ["labels"] = labels,
        };
        var voice = StrOf(o["voice"], 16);
        if (voice.Length > 0) p["voice"] = voice;
        if (actions is not null) p["actions"] = actions;
        if (meta is not null) p["meta"] = meta;
        return (p, "");
    }

    /// <summary>long/double 子动作递归白名单（深度一层：值/文本裁剪，类型不审 ——
    /// 执行期白名单兜底，这里只防体积膨胀）。</summary>
    private static void StripAction(JsonObject a)
    {
        var keep = new[] { "type", "value", "text", "mods", "steps" };
        foreach (var k in a.Select(x => x.Key).Where(k => !keep.Contains(k)).ToList()) a.Remove(k);
        if (a["value"] is { } v) a["value"] = StrOf(v, 64);
        if (a["text"] is { } t) a["text"] = StrOf(t, 200);
    }

    // ================= 本地查找（本机 > 打包）=================

    /// <summary>档案与设备的匹配分（高=优）。-1 = 不匹配：vid/pid 不同，或档案带
    /// names 而设备名已知且不中（同 VID/PID 异型号防线，语义对齐 DeviceDb.Lookup）。
    /// rev 精确命中 4 分；档案无 revs（通用档案）2 分；设备 rev 未知而档案限定 rev
    /// 1 分（宽容：注册表暂时读不到 REV 不至于把档案全打翻）。</summary>
    internal static int Score(JsonObject p, string? vid, string? pid, string? rev, string? name)
    {
        var d = p["device"] as JsonObject;
        if (!string.Equals(Norm(d?["vid"]?.GetValue<string>()), Norm(vid), StringComparison.Ordinal)
            || !string.Equals(Norm(d?["pid"]?.GetValue<string>()), Norm(pid), StringComparison.Ordinal))
            return -1;
        var names = d?["names"] as JsonArray;
        if (names is { Count: > 0 } && !string.IsNullOrWhiteSpace(name)
            && !names.Any(n => name!.Contains(n?.GetValue<string>() ?? "",
                              StringComparison.OrdinalIgnoreCase)))
            return -1;
        var revs = d?["revs"] as JsonArray;
        if (revs is { Count: > 0 })
        {
            if (string.IsNullOrWhiteSpace(rev)) return 1;
            return revs.Any(r => string.Equals(Norm(r?.GetValue<string>()), Norm(rev),
                              StringComparison.Ordinal)) ? 4 : -1;
        }
        return 2;
    }

    /// <summary>按设备身份查档案：本机目录 → 打包内嵌资源。返回 (档案, 来源)；
    /// 来源 "local"/"packaged"/"none"。坏 JSON 记日志跳过，绝不拖垮查找。</summary>
    internal static (JsonObject? P, string Source) Lookup(string? vid, string? pid, string? rev, string? name)
    {
        JsonObject? best = null; var bestSrc = "none"; var bestScore = -1;
        void Consider(JsonObject p, string src)
        {
            var s = Score(p, vid, pid, rev, name);
            if (s > bestScore) { bestScore = s; best = p; bestSrc = src; }
        }
        try
        {
            foreach (var f in Directory.EnumerateFiles(LocalDir, "*.json")
                         .Where(f => !Path.GetFileName(f).StartsWith('.')))
            {
                try
                {
                    if (Parse(File.ReadAllText(f)) is { P: { } p, Err: "" }) Consider(p, "local");
                }
                catch { /* 单个坏文件跳过（设计稿 §4） */ }
            }
        }
        catch { /* 目录不存在 = 没有本机档案 */ }
        try
        {
            foreach (var rn in typeof(ProfileStore).Assembly.GetManifestResourceNames()
                         .Where(n => n.StartsWith("VibeMate.profile.", StringComparison.Ordinal)))
            {
                try
                {
                    using var st = typeof(ProfileStore).Assembly.GetManifestResourceStream(rn);
                    using var r = new StreamReader(st!);
                    if (Parse(r.ReadToEnd()) is { P: { } p, Err: "" }) Consider(p, "packaged");
                }
                catch { /* 单个坏资源跳过 */ }
            }
        }
        catch { /* 枚举失败（无打包档案）→ 静默 */ }
        return (best, bestSrc);
    }

    // ================= 落盘（本机档案 + 备份）=================

    /// <summary>档案文件名：单 rev → vid-pid-rev.json，多 rev/通用 → vid-pid.json
    ///（导出附件名与落盘名共用；导入时校验文件名前缀与身份一致，防改名张冠李戴）。</summary>
    internal static string FileNameOf(JsonObject p)
    {
        var d = (JsonObject)p["device"]!;
        var revs = (JsonArray?)d["revs"];
        return revs is { Count: 1 }
            ? $"{Norm(d["vid"]!.GetValue<string>())}-{Norm(d["pid"]!.GetValue<string>())}-{Norm(revs[0]!.GetValue<string>())}.json"
            : $"{Norm(d["vid"]!.GetValue<string>())}-{Norm(d["pid"]!.GetValue<string>())}.json";
    }

    /// <summary>写入本机档案（原子替换，对齐 ConfigService 的断电纪律）。已有同名
    /// 文件先备份到 .backup\（最多留 3 份）。meta.source 由调用方填（learned/imported/cloud）。</summary>
    internal static (bool Ok, string Msg) Save(JsonObject profile, string source)
    {
        try
        {
            Directory.CreateDirectory(LocalDir);
            var file = Path.Combine(LocalDir, FileNameOf(profile));
            if (File.Exists(file))
            {
                try
                {
                    Directory.CreateDirectory(BackupDir);
                    var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    File.Copy(file, Path.Combine(BackupDir, Path.GetFileName(file) + "." + stamp), true);
                    // 只留最近 3 份备份（按名排序=按时间，旧的前面）
                    var olds = Directory.GetFiles(BackupDir, Path.GetFileName(file) + ".*")
                                        .OrderBy(f => f).ToList();
                    foreach (var old in olds.Take(Math.Max(0, olds.Count - 3))) File.Delete(old);
                }
                catch { /* 备份失败不挡写入（旧内容本来就要被替换） */ }
            }
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, profile.ToJsonString(ConfigService.JsonOpts));
            if (File.Exists(file)) File.Replace(tmp, file, null);
            else File.Move(tmp, file);
            return (true, Path.GetFileName(file));
        }
        catch (Exception e) { return (false, $"写入失败：{e.Message}"); }
    }

    /// <summary>学习/导出档案共用的 device 节点（身份来自注册表，vid/pid/rev 规整小写）。</summary>
    private static JsonObject DeviceNode((string Vid, string Pid, string Rev, string Name) id) => new()
    {
        ["vid"] = Norm(id.Vid), ["pid"] = Norm(id.Pid),
        ["revs"] = string.IsNullOrWhiteSpace(id.Rev) ? new JsonArray() : new JsonArray { Norm(id.Rev) },
        ["names"] = id.Name.Length > 0 ? new JsonArray { id.Name } : new JsonArray(),
    };

    /// <summary>学习/导出档案共用的 meta（来源 learned，note 带 rev 与系统版本便于排障）。</summary>
    private static JsonObject LearnedMeta(string rev) => new()
    {
        ["updated"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
        ["source"] = "learned",
        ["note"] = $"rev {rev}, Windows {Environment.OSVersion.Version}",
    };

    /// <summary>学习成果双写（设计稿 §3）：地址级配置（config.devices[addr]，现有
    /// 行为）之外，同时生成/合并可移植档案 —— 逐键保存会反复进来，labels 与已有
    /// 档案合并、report 整体替换。身份/蓝牙名读不到注册表时静默跳过（不影响学习）。</summary>
    internal static void SaveLearned(string addr, JsonObject devNode)
    {
        try
        {
            if (IdentityOfAddr(addr) is not { } id) return;
            // 已有本机档案先取来合并（学过一半再学新键，labels 不能互相冲掉）
            var (exist, _) = Lookup(id.Vid, id.Pid, id.Rev, id.Name);
            var p = exist is { } e && Score(e, id.Vid, id.Pid, id.Rev, id.Name) > 0
                ? (JsonObject)e.DeepClone() : null;
            if (p is null || p["device"] is not JsonObject)
                p = new JsonObject { ["schemaVersion"] = 1, ["device"] = DeviceNode(id) };
            if (devNode["report"] is { } rep) p["report"] = rep.DeepClone();
            var labels = p["labels"] as JsonObject ?? new JsonObject();
            foreach (var (k, v) in devNode["labels"] as JsonObject ?? new JsonObject())
                if (UsageOf(k) is { } u) labels[$"0x{u:X4}"] = v?.GetValue<string>() ?? "";
            p["labels"] = labels;
            // 首键保存前指纹可能还没推导出来：没有 report 的档案下次加载会被
            // Parse 拒收，干脆先不落盘（地址级配置已保存，下一键保存会补上）
            if (p["report"] is null) return;
            p["meta"] = LearnedMeta(id.Rev);
            var (ok, msg) = Save(p, "learned");
            if (ok) Program.Log("PROFILE", $"学习成果已写入可移植档案 {msg}");
        }
        catch (Exception e) { Program.Log("PROFILE", $"学习成果写档案失败（不影响学习）：{e.Message}"); }
    }

    /// <summary>导出当前设备的可移植档案（设置页「导出」按钮）：身份/指纹/键名来自
    /// config.devices[addr] 的学习成果，动作预设默认不含（勾选才带，且只带 usage
    /// 出现在本档案 labels 里的映射 —— 全局 keys 里可能混着别的遥控器的自定义）。
    /// 返回 null = 该设备还没有可导出的学习成果。</summary>
    internal static JsonObject? BuildExport(ConfigService cfg, string addr, bool withActions)
    {
        var snap = cfg.Snapshot();
        var dev = (snap["devices"] as JsonObject)?[addr] as JsonObject;
        if (dev?["report"] is null) return null;
        if (IdentityOfAddr(addr) is not { } id) return null;
        var p = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["device"] = DeviceNode(id),
            ["report"] = dev["report"]!.DeepClone(),
            ["labels"] = dev["labels"]?.DeepClone() ?? new JsonObject(),
        };
        if (withActions && dev["labels"] is JsonObject labels)
        {
            var usages = labels.Select(x => UsageOf(x.Key)).Where(u => u is not null)
                               .Select(u => $"0x{u!:X4}".ToLowerInvariant()).ToHashSet();
            var acts = new JsonObject();
            foreach (var (name, m) in snap["keys"] as JsonObject ?? new JsonObject())
            {
                if (m is not JsonObject m2) continue;
                var u = $"0x{UsageOf(m2["usage"]?.GetValue<string>()):X4}".ToLowerInvariant();
                if (usages.Contains(u)) acts[name] = m2.DeepClone();
            }
            if (acts.Count > 0) p["actions"] = acts;
        }
        p["meta"] = LearnedMeta(id.Rev);
        return p;
    }

    // ================= 云端索引与查询 =================

    private static readonly object CloudLock = new();
    private static JsonObject? _index;                        // 云端索引（缓存/拉取）
    private static DateTime _indexAt;                         // 索引获取时间（12h 过期）
    private static readonly Dictionary<string, JsonObject> Queries = new();   // deviceId → 查询状态
    private static readonly Dictionary<string, JsonObject> Pending = new();   // deviceId → 下载暂存

    private static void LoadIndexCache()
    {
        try
        {
            if (File.Exists(IndexPath)
                && JsonNode.Parse(File.ReadAllText(IndexPath)) is JsonObject o
                && o["devices"] is JsonObject)
                lock (CloudLock) { _index = o; _indexAt = File.GetLastWriteTimeUtc(IndexPath); }
        }
        catch { /* 坏缓存 → 当作没有（首次查询会现拉） */ }
    }

    /// <summary>启动后台刷新索引（对齐 DeviceDb.RefreshAsync：绝不阻塞启动）。</summary>
    internal static void RefreshIndexAsync() => _ = Task.Run(async () =>
    {
        if ((DateTime.UtcNow - _indexAt).TotalHours < 12) return;   // 还新鲜
        await FetchIndexAsync();
    });

    private static async Task<bool> FetchIndexAsync()
    {
        foreach (var url in IndexUrls)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var text = await http.GetStringAsync(url);
                if (JsonNode.Parse(text) is JsonObject o && o["devices"] is JsonObject)
                {
                    Directory.CreateDirectory(CloudDir);
                    await File.WriteAllTextAsync(IndexPath, text);
                    lock (CloudLock) { _index = o; _indexAt = DateTime.UtcNow; }
                    return true;
                }
            }
            catch { /* 下一源 */ }
        }
        return false;
    }

    /// <summary>启动云端查询（后台，立即返回）：索引过期先拉索引，再按 deviceId
    ///（含 vid:pid:* 通用键回退）查条目。状态进 Queries 供前端轮询 —— HTTP 单线程
    /// 串行，网络等待绝不能占着 Loop（cable/install 的同款纪律）。</summary>
    internal static JsonObject StartCloudQuery(string vid, string pid, string rev)
    {
        var id = DeviceIdOf(vid, pid, rev);
        lock (CloudLock)
        {
            // 查过/在查：不重复（found 的重新下载走 import.download）
            if (Queries.TryGetValue(id, out var cur)
                && cur["status"]?.GetValue<string>() is "querying" or "found" or "none")
                return cur;
            Queries[id] = new JsonObject { ["status"] = "querying" };
        }
        _ = Task.Run(async () =>
        {
            try
            {
                if ((DateTime.UtcNow - _indexAt).TotalHours >= 12 && !await FetchIndexAsync())
                {
                    lock (CloudLock) Queries[id] = new JsonObject
                    { ["status"] = "error", ["msg"] = "云端索引不可达（离线或被墙）" };
                    return;
                }
                JsonObject? entry;
                lock (CloudLock)
                {
                    var devs = _index?["devices"] as JsonObject;
                    entry = devs?[id] as JsonObject ?? devs?[GenericId(id)] as JsonObject;
                }
                if (entry is null)
                {
                    lock (CloudLock) Queries[id] = new JsonObject
                    { ["status"] = "none", ["checked"] = DateTime.Now.ToString("HH:mm") };
                    return;
                }
                var file = entry["file"]?.GetValue<string>() ?? "";
                if (file.Length is 0 or > 128 || file.Contains('/') || file.Contains('\\')
                    || !file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    lock (CloudLock) Queries[id] = new JsonObject
                    { ["status"] = "error", ["msg"] = "索引条目损坏" };
                    return;
                }
                var sha = new string((entry["sha256"]?.GetValue<string>() ?? "")
                    .Where(char.IsLetterOrDigit).Take(64).ToArray());
                var meta = new JsonObject
                {
                    ["status"] = "found",
                    ["file"] = file,
                    ["sha256"] = sha,
                    ["author"] = San(entry["author"]?.GetValue<string>()),
                    ["updated"] = San(entry["updated"]?.GetValue<string>()),
                    ["buttons"] = IntOf(entry["buttons"]) ?? 0,
                };
                lock (CloudLock) Queries[id] = meta;
            }
            catch (Exception e)
            {
                lock (CloudLock) Queries[id] = new JsonObject
                { ["status"] = "error", ["msg"] = e.Message };
            }
        });
        return new JsonObject { ["status"] = "querying" };
    }

    /// <summary>云端元数据消毒：只留字母数字与少量标点（author/updated 会被前端
    /// 插进 innerHTML —— 云端内容按不可信输入处理，白名单比转义更省心）。</summary>
    private static string San(string? s) =>
        new((s ?? "").Where(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or ' ')
            .Take(40).ToArray());

    /// <summary>查询状态快照（GET /api/device-profile 用；未查询过返回 idle）。</summary>
    internal static JsonObject CloudInfo(string deviceId)
    {
        lock (CloudLock)
        {
            if (Queries.TryGetValue(deviceId, out var q)) return (JsonObject)q.DeepClone();
            return new JsonObject { ["status"] = "idle" };
        }
    }

    /// <summary>启动云端档案下载（import 第一段，后台）：结果存 Pending 供前端
    /// 轮询与应用段取用。下载产物已过 DownloadAsync 的三道校验。</summary>
    internal static void StartDownload(string deviceId)
    {
        lock (CloudLock) Pending[deviceId] = new JsonObject { ["status"] = "downloading" };
        _ = Task.Run(async () =>
        {
            var (p, err) = await DownloadAsync(deviceId);
            lock (CloudLock) Pending[deviceId] = p is not null
                ? new JsonObject { ["status"] = "ready", ["profile"] = p }
                : new JsonObject { ["status"] = "error", ["msg"] = err };
        });
    }

    /// <summary>下载状态快照（GET /api/device-profile 的 pending 字段 —— 给前端
    /// 渲染预览/确认框用，只露摘要不带整个档案）。</summary>
    internal static JsonObject PendingInfo(string deviceId)
    {
        lock (CloudLock)
        {
            if (!Pending.TryGetValue(deviceId, out var v))
                return new JsonObject { ["status"] = "none" };
            var o = new JsonObject
            {
                ["status"] = v["status"]?.GetValue<string>() ?? "none",
                ["buttons"] = (v["profile"]?["labels"] as JsonObject)?.Count ?? 0,
                ["hasActions"] = v["profile"]?["actions"] is JsonObject,
            };
            if (v["msg"] is { } m) o["msg"] = m.DeepClone();
            if (v["profile"]?["device"] is JsonObject d)
            {
                o["vid"] = d["vid"]?.GetValue<string>();
                o["pid"] = d["pid"]?.GetValue<string>();
            }
            return o;
        }
    }

    /// <summary>取下载完成的档案（import 第二段）。没下载完/失败返回错误串。</summary>
    internal static (JsonObject? P, string Err) TakePending(string deviceId)
    {
        lock (CloudLock)
        {
            if (!Pending.TryGetValue(deviceId, out var v))
                return (null, "请先下载（导入第一段）");
            var st = v["status"]?.GetValue<string>();
            if (st == "ready" && v["profile"] is JsonObject p) return (p, "");
            if (st == "downloading") return (null, "还在下载中，稍候再试");
            return (null, v["msg"]?.GetValue<string>() ?? "下载失败");
        }
    }

    /// <summary>下载云端档案（import.download 确认后调用）：按索引 sha256 校验、
    /// Parse 白名单校验、身份与请求的 deviceId 核对 —— 三道全过才给档案。</summary>
    internal static async Task<(JsonObject? P, string Err)> DownloadAsync(string deviceId)
    {
        JsonObject? entry;
        lock (CloudLock)
            entry = (Queries.TryGetValue(deviceId, out var q)
                     && q["status"]?.GetValue<string>() == "found") ? q : null;
        if (entry is null) return (null, "云端查询还没完成（先 lookup）");
        var file = entry["file"]!.GetValue<string>();
        var wantSha = entry["sha256"]?.GetValue<string>() ?? "";
        foreach (var url in FileUrls(file))
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var bytes = await http.GetByteArrayAsync(url);
                if (bytes.Length > 256 * 1024) return (null, "云端档案超过 256KB");
                if (wantSha.Length == 64
                    && Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != wantSha)
                    continue;                     // 哈希不符 → 换源再试（CDN 缓存陈旧）
                var (p, err) = Parse(Encoding.UTF8.GetString(bytes));
                if (p is null) return (null, "云端档案校验失败：" + err);
                // 身份核对：请求 18d1:9450:0110，档案必须是 18d1:9450 且 revs 含 0110
                //（或通用档案）；vid/pid 不同的档案（索引被改）直接拒收
                var d = (JsonObject)p["device"]!;
                var idParts = deviceId.Split(':');
                if (!string.Equals(Norm(d["vid"]!.GetValue<string>()), idParts[0], StringComparison.Ordinal)
                    || !string.Equals(Norm(d["pid"]!.GetValue<string>()), idParts[1], StringComparison.Ordinal))
                    return (null, "云端档案身份与请求不符（拒收）");
                if (idParts[2] != "*")
                {
                    var revs = (JsonArray?)d["revs"];
                    var hit = revs is null || revs.Count == 0
                              || revs.Any(r => string.Equals(Norm(r?.GetValue<string>()), idParts[2],
                                                     StringComparison.Ordinal));
                    if (!hit) return (null, "云端档案 rev 与请求不符（拒收）");
                }
                return (p, "");
            }
            catch { /* 下一源 */ }
        }
        return (null, "下载失败（网络不可达或校验不符）");
    }
}
