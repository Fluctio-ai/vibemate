using System.Text.Json;
using System.Text.Json.Nodes;

namespace VibeMate;

/// <summary>
/// 配置服务 —— config.json 的唯一读者/写者（v1 的血泪教训：两处写共用一个
/// tmp 文件名会互相覆盖、或在目录里留下带完整配置的残留 tmp）。
///
/// 规则（设计文档 §3）：
///   · 单点写入：所有修改都走本类内部的一把锁；
///   · 原子替换：写 .tmp → File.Replace，断电不留半个文件；
///   · 热推：落盘后触发 ConfigChanged，各角色自行响应（改映射即时生效）。
/// </summary>
public sealed class ConfigService : IDisposable
{
    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _path;
    private readonly object _lock = new();
    private JsonObject _root;

    /// <summary>配置成功变更并落盘后触发（参数=最新配置根节点，只读约定）。</summary>
    public event Action<JsonObject>? ConfigChanged;

    public ConfigService(string path)
    {
        _path = path;
        _root = Load();
    }

    // ---------- 读 ----------
    /// <summary>当前配置的浅拷贝（跨线程安全：返回深拷贝节点）。</summary>
    public JsonObject Snapshot()
    {
        lock (_lock)
            return JsonNode.Parse(_root.ToJsonString(JsonOpts))!.AsObject();
    }

    public T Get<T>(string key, T fallback)
    {
        lock (_lock)
        {
            var v = _root[key];
            if (v is null) return fallback;
            try { return v.GetValue<T>(); } catch { return fallback; }
        }
    }

    // ---------- 写 ----------
    /// <summary>
    /// 用前端提交的补丁更新配置：只接受已知字段（白名单合并，而不是整树替换——
    /// 陌生键一概丢弃，防止坏数据入库），校验通过才落盘。
    /// 返回 (ok, message)。
    /// </summary>
    public (bool Ok, string Msg) Apply(JsonObject patch)
    {
        JsonObject merged;
        lock (_lock)
        {
            merged = JsonNode.Parse(_root.ToJsonString(JsonOpts))!.AsObject();
            foreach (var (k, v) in patch)
            {
                // 白名单：v1 兼容字段 + v2 devices + cable_optout（卸载标记，见 Program 看护）
                if (k is "ui_port" or "remote_addr"
                    or "voice" or "audio" or "keys" or "devices" or "_comment"
                    or "cable_optout")
                    merged[k] = v?.DeepClone();
            }
        }

        var (valid, why) = Validate(merged);
        if (!valid) return (false, why);

        lock (_lock) { _root = merged; }        // 内存先换（读端立刻一致）
        WriteAtomic(merged);                    // 磁盘写锁外：读端（按键热路径/轮询）不陪 IO 排队
        ConfigChanged?.Invoke(merged);          // 只读约定（见事件注释），省一次全树深拷贝
        return (true, "saved");
    }

    /// <summary>基础校验：类型/范围。深结构（keys/devices 的映射内容）由调用方细化。</summary>
    private static (bool, string) Validate(JsonObject cfg)
    {
        if (cfg["ui_port"] is { } p)
        {
            if (!int.TryParse(p.ToJsonString(), out var port)
                || port is < 1 or > 65535)
                return (false, "ui_port 必须是 1-65535");
        }
        if (cfg["cable_optout"] is { } co
            && co.GetValueKind() != JsonValueKind.True && co.GetValueKind() != JsonValueKind.False)
            return (false, "cable_optout 必须是布尔值");
        return (true, "");
    }

    /// <summary>
    /// 学习成果落库：devices[addr] 增量合并（report 指纹整体替换、labels 逐键合并）。
    /// 学习是逐键保存的 —— 整树替换会互相冲掉，必须 merge。
    /// </summary>
    public (bool Ok, string Msg) SetDevice(string addr, JsonObject node)
    {
        if (string.IsNullOrWhiteSpace(addr)) return (false, "设备地址为空");
        JsonObject merged;
        lock (_lock)
        {
            merged = Snapshot();                // Monitor 可重入：锁内直接复用既有深拷贝
            var devices = merged["devices"] as JsonObject ?? new JsonObject();
            var cur = devices[addr] as JsonObject ?? new JsonObject();
            foreach (var (k, v) in node)
            {
                if (k == "labels" && v is JsonObject labels
                    && cur["labels"] is JsonObject curLabels)
                {
                    foreach (var (lk, lv) in labels) curLabels[lk] = lv?.DeepClone();
                }
                else cur[k] = v?.DeepClone();
            }
            devices[addr] = cur;
            merged["devices"] = devices;
            _root = merged;
        }
        WriteAtomic(merged);                    // 磁盘写锁外（同 Apply：读端不陪 IO 排队）
        ConfigChanged?.Invoke(merged);
        return (true, "saved");
    }

    // ---------- 落盘 ----------
    private JsonObject Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var root = JsonNode.Parse(File.ReadAllText(_path));
                if (root is JsonObject o) return o;
            }
        }
        catch { /* 坏文件 → 走默认 */ }
        // 没有配置文件（或坏了）：默认配置直接写出 —— 用户拿到完整可编辑的
        // config.json，而不是「内存里有、盘上看不到」
        var def = DefaultConfig();
        try { WriteAtomic(def); } catch { /* 盘只读等极端情况：内存默认继续跑 */ }
        return def;
    }

    internal static JsonObject DefaultConfig()
    {
        // 出厂默认（2026-09-16 用户定稿版 = release/config.json 的全套映射）：
        // 方向键=方向键、返回=短按删一下/长按连删、电源/YouTube/Netflix=WorkBuddy
        // 文本命令、音量=Ctrl+滚轮、语音键=Win+Ctrl 按住说话。
        // ★ remote_addr 不预置 —— 那是个人蓝牙地址，装到别人机器上只会瞎连。
        static JsonArray Mods(params string[] mods)
        {
            var a = new JsonArray();
            foreach (var m in mods) a.Add(m);
            return a;
        }
        static JsonObject M(string type, string value, string usage,
                            string[]? mods = null, string text = "") => new()
        {
            ["type"] = type, ["value"] = value,
            ["mods"] = Mods(mods ?? Array.Empty<string>()),
            ["text"] = text, ["usage"] = usage,
        };
        var back = M("key", "BACKSPACE", "0x0224");
        back["long"] = new JsonObject               // 长按连删（long 子对象不带 usage）
        {
            ["type"] = "key", ["value"] = "BACKSPACE",
            ["mods"] = new JsonArray(), ["text"] = "",
        };
        var keys = new JsonObject
        {
            ["ok"]      = M("key", "ENTER", "0x0041"),
            ["back"]    = back,
            ["up"]      = M("key", "UP", "0x0042"),
            ["down"]    = M("key", "DOWN", "0x0043"),
            ["left"]    = M("key", "LEFT", "0x0044"),
            ["right"]   = M("key", "RIGHT", "0x0045"),
            ["input"]   = M("combo", "TAB", "0x0189", new[] { "SHIFT" }),
            ["power"]   = M("text", "ENTER", "0x019E", new[] { "CTRL" }, "/exit"),
            ["voldown"] = M("mouse", "wheel_down", "0x00EA", new[] { "CTRL" }),
            ["volup"]   = M("mouse", "wheel_up", "0x00E9", new[] { "CTRL" }),
            ["home"]    = M("key", "ESC", "0x0223"),
            ["youtube"] = M("text", "ENTER", "0x0077", new[] { "CTRL" }, "/clear"),
            ["mute"]    = M("key", "ENTER", "0x00E2"),
            ["netflix"] = M("text", "ENTER", "0x0078", new[] { "CTRL" }, "commit+push"),
        };
        return new JsonObject
        {
            ["_comment"] = "VibeMate v2 配置。唯一权威；界面保存即写回此文件。",
            ["ui_port"] = 8787,
            ["remote_addr"] = "",
            ["voice"] = new JsonObject { ["mode"] = "hold", ["key"] = "CTRL",
                                         ["mods"] = Mods("LWIN") },
            ["audio"] = new JsonObject { ["gain"] = 4.0, ["agc"] = true, ["relay"] = true },
            ["keys"] = keys,
            ["devices"] = new JsonObject(),
        };
    }

    private void WriteAtomic(JsonObject root)
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, root.ToJsonString(JsonOpts));
        if (File.Exists(_path))
            File.Replace(tmp, _path, null);
        else
            File.Move(tmp, _path);
    }

    public void Dispose()
    {
        // 没有需要释放的句柄；保留接口给将来（如文件监听）
        ConfigChanged = null;
    }
}
