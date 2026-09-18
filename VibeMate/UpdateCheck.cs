using System.Net.Http;
using System.Text.Json.Nodes;

namespace VibeMate;

/// <summary>
/// 版本更新检查：后台定时问 GitHub Releases 最新 tag，结果缓存进 /api/state。
///
/// 检查在服务端做而不是页面里做 —— 控制台是「零外部资源」纪律（web/index.html
/// 全内联），让它 fetch 外网等于给页面开了外联口子；且离线/被墙时服务端静默
/// 置空即可，用户界面永远不卡外网请求。
/// </summary>
internal static class UpdateCheck
{
    // tag 推上来时 CI 自动建 Release（.github/workflows），latest 永远指向最新正式版
    private const string LatestUrl = "https://api.github.com/repos/Fluctio-ai/vibemate/releases/latest";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    // volatile：HTTP 线程写、/api/state 读。latest null = 还没查到/查不了（离线、被墙、限流）。
    // available 在 CheckOnce 算好 —— Snapshot 每 2s 被问一次，别每次重解析两个 Version 串
    private static volatile string? _latest;
    private static volatile bool _available;

    /// <summary>启动后台循环（Main 调一次）。首查延迟避开启动高峰，之后 6h 一拍。
    /// 失败保持上次结果不重试加速 —— 版本检查没有时效性，没必要对不可达的
    /// api.github.com 空转重连。</summary>
    internal static void Start()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            while (true)
            {
                await CheckOnce();
                await Task.Delay(TimeSpan.FromHours(6));
            }
        });
    }

    private static async Task CheckOnce()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
            req.Headers.UserAgent.ParseAdd($"VibeMate/{Program.AppVersion}");   // GitHub API 无 UA 直接 403
            using var resp = await Http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync());
            var tag = json?["tag_name"]?.GetValue<string>() ?? "";
            var ver = tag.TrimStart('v', 'V');
            // 解析不出三段版本号的 tag（将来若有 -beta 之类）只按能解析的部分记
            if (Version.TryParse(ver, out var l))
            {
                _latest = ver;
                _available = Version.TryParse(Program.AppVersion, out var cur) && l > cur;
            }
        }
        catch { /* 离线/被墙/限流：保持上次结果 */ }
    }

    /// <summary>/api/state 的 update 段。available = latest 严格大于当前程序集版本。</summary>
    internal static JsonObject Snapshot()
        => new() { ["latest"] = _latest, ["available"] = _available };
}
