namespace VibeMate;

/// <summary>
/// 云端源的唯一拼装处（DeviceDb 的 devices.json 与 ProfileStore 的 profiles/
/// 共用 —— 改仓库/路径只动这里）。多源轮询顺序：
///   用户自填加速前缀（config.gh_proxy，若填）→ jsdelivr CDN →
///   raw.githubusercontent.com 直连 → ghfast.top / gh-proxy.org 反代。
/// 逐源 5s 超时、失败静默换下一个（轮询循环在各自调用方，这里只管给名单）。
/// </summary>
internal static class CloudSource
{
    private const string Repo = "Fluctio-ai/vibemate";
    private const string Branch = "main";

    // GitHub 反代加速（前缀直接拼在完整 raw URL 之前）：ghfast.top 是 git remote
    // 同款反代（本机实测可达），gh-proxy.org 社区常用。可达性随网络波动，仅作兜底。
    private static readonly string[] ProxyPrefixes =
    {
        "https://ghfast.top/",
        "https://gh-proxy.org/",
    };

    /// <summary>用户自填加速前缀（如 https://gh-proxy.org/），轮询时最优先；
    /// 由 Program 启动时与配置变更时刷新。null/空白 = 未启用。</summary>
    internal static string? CustomPrefix;

    /// <summary>按仓库相对路径（如 "devices.json"、"profiles/index.json"）
    /// 拼全部候选 URL，顺序即轮询优先级。</summary>
    internal static string[] UrlsFor(string path)
    {
        var raw = $"https://raw.githubusercontent.com/{Repo}/{Branch}/{path}";
        var urls = new List<string>();
        var custom = CustomPrefix?.Trim();
        if (!string.IsNullOrEmpty(custom))
            urls.Add(custom!.TrimEnd('/') + "/" + raw);
        urls.Add($"https://cdn.jsdelivr.net/gh/{Repo}@{Branch}/{path}");
        urls.Add(raw);
        foreach (var pre in ProxyPrefixes) urls.Add(pre + raw);
        return urls.ToArray();
    }
}
