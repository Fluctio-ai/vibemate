using System.Text.Json.Nodes;

namespace VibeMate;

/// <summary>
/// 已知遥控器指纹库（devices.json）。加载优先级：云端缓存（ProgramData，启动后台刷新）
/// &gt; 内置 exe 资源 —— 离线/网络差时全功能可用。
///
/// 表只是「预填键名的便利层」：未收录设备走界面学习模式（按键 3 次确认 + 指纹
/// 自动推导），语音能力走 ATVV 运行时探测（连上枚举 GATT 服务）—— 两者都不
/// 依赖本表，表缺失或过时不会导致功能不可用。
///
/// 云端地址多源轮询（对齐 pip 镜像表的思路）：jsdelivr 的国内可达性一般优于
/// raw.githubusercontent.com 直连；都失败静默（下次启动再试）。
/// </summary>
internal static class DeviceDb
{
    private static JsonObject? _table;
    private static readonly object Lock = new();

    private static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "VibeMate", "devices.json");

    // 表随仓库走（mumutoy/vibe-mote 根目录 devices.json）；改仓库/表路径只动这里
    private static readonly string[] RemoteUrls =
    {
        "https://cdn.jsdelivr.net/gh/mumutoy/vibe-mote@main/devices.json",
        "https://raw.githubusercontent.com/mumutoy/vibe-mote/main/devices.json",
    };

    static DeviceDb() => Load();

    private static void Load()
    {
        try
        {
            if (File.Exists(CachePath)
                && JsonNode.Parse(File.ReadAllText(CachePath)) is JsonObject cached)
            {
                lock (Lock) _table = cached;
                return;
            }
        }
        catch { /* 坏缓存 → 走内置 */ }
        try
        {
            using var st = typeof(DeviceDb).Assembly
                .GetManifestResourceStream("VibeMate.devices.json");
            if (st is not null)
            {
                using var r = new StreamReader(st);
                if (JsonNode.Parse(r.ReadToEnd()) is JsonObject builtIn)
                {
                    lock (Lock) _table = builtIn;
                    return;
                }
            }
        }
        catch { /* 内置也缺（构建配置错）→ Lookup 恒 miss，学习模式兜底 */ }
    }

    /// <summary>启动后台刷新：拉到合法表才落缓存（版本管理交给 URL @main，
    /// 缓存整体替换）。绝不阻塞启动 —— 表是便利层，不是依赖。</summary>
    public static void RefreshAsync()
    {
        _ = Task.Run(async () =>
        {
            foreach (var url in RemoteUrls)
            {
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                    var text = await http.GetStringAsync(url);
                    if (JsonNode.Parse(text) is JsonObject o && o["devices"] is JsonArray)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
                        await File.WriteAllTextAsync(CachePath, text);
                        lock (Lock) _table = o;
                        Program.Log("INFO", $"设备表已更新（{url}）");
                        return;
                    }
                }
                catch { /* 下一源 */ }
            }
        });
    }

    /// <summary>按 VID/PID 查表。命中返回 {vid,pid,brand,model,voice,report,keys}
    /// 的拷贝；未命中 null（调用方引导学习模式）。</summary>
    public static JsonObject? Lookup(string? vid, string? pid)
    {
        if (string.IsNullOrWhiteSpace(vid) || string.IsNullOrWhiteSpace(pid)) return null;
        JsonObject? arr;
        lock (Lock) arr = _table;
        var devices = arr?["devices"] as JsonArray;
        if (devices is null) return null;
        foreach (var d in devices.OfType<JsonObject>())
        {
            if (string.Equals(d["vid"]?.GetValue<string>(), vid, StringComparison.OrdinalIgnoreCase)
                && string.Equals(d["pid"]?.GetValue<string>(), pid, StringComparison.OrdinalIgnoreCase))
                return (JsonObject)d.DeepClone();
        }
        return null;
    }
}
