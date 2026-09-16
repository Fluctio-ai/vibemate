using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace VibeMate;

/// <summary>
/// HTTP 服务 —— 只监听 127.0.0.1（v1 纪律：绑 0.0.0.0 会触发防火墙弹窗）。
/// 静态页 + 极简 JSON API。
///
/// 安全（对齐 v1 的两道闸）：POST 必须 application/json 且 Origin/Referer
/// 为空或属于本机 —— 防 drive-by CSRF（no-cors 的 text/plain POST 不走预检）。
/// </summary>
public sealed class HttpServer
{
    private readonly HttpListener _listener = new();
    private readonly string _webRoot;
    private readonly string _logPath;
    private readonly ConfigService _config;
    private readonly Func<JsonObject> _stateBuilder;
    private readonly Func<int, JsonObject> _eventsProvider;
    private readonly KeysRole? _keys;           // 学习模式控制（start/stop；快照走 stateBuilder）
    private Thread? _thread;
    private volatile bool _running;

    public int Port { get; }

    public HttpServer(int port, string webRoot, string logPath, ConfigService config,
                      Func<JsonObject> stateBuilder,
                      Func<int, JsonObject>? eventsProvider = null,
                      KeysRole? keys = null)
    {
        Port = port;
        _webRoot = webRoot;
        _logPath = logPath;
        _config = config;
        _stateBuilder = stateBuilder;
        _keys = keys;
        _eventsProvider = eventsProvider ?? (_ => new JsonObject { ["events"] = new JsonArray(), ["last"] = 0 });
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    public void Start()
    {
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "http" };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        try { _listener.Stop(); } catch { /* 已停 */ }
    }

    private void Loop()
    {
        try { _listener.Start(); }
        catch (Exception e)
        {
            Console.Error.WriteLine($"HTTP 启动失败（端口 {Port} 被占？）：{e.Message}");
            return;
        }
        while (_running)
        {
            HttpListenerContext ctx;
            try { ctx = _listener.GetContext(); }
            catch (Exception) { break; }   // listener.Stop() 会走到这
            try { Handle(ctx); }
            catch (Exception e)
            {
                // 任何路由异常都变成一条 JSON 回复（v1 的教训：直接断连接
                // 用户只会看到「连接被关闭」，毫无线索）
                try { ReplyJson(ctx, 500, new JsonObject { ["ok"] = false, ["msg"] = $"{e.GetType().Name}: {e.Message}" }); }
                catch { /* 客户端跑了 */ }
            }
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var path = req.Url!.AbsolutePath;

        if (req.HttpMethod == "GET")
        {
            switch (path)
            {
                case "/api/ping":
                    ReplyJson(ctx, 200, new JsonObject { ["ok"] = true, ["version"] = "2.1.1" });
                    return;
                case "/api/state":
                    ReplyJson(ctx, 200, _stateBuilder());
                    return;
                case "/api/learn":
                    // 学习状态专用轻量端点：前端学习期间 300ms 轮询只要这几个字段，
                    // 走 /api/state 会每次全配置深拷贝+序列化（HTTP 单线程串行，会顶住）
                    ReplyJson(ctx, 200, _keys?.LearnSnapshot()
                              ?? new JsonObject { ["learning"] = false });
                    return;
                case "/api/config":
                    ReplyJson(ctx, 200, _config.Snapshot());
                    return;
                case "/api/log":
                    ServeLog(ctx);
                    return;
                case "/api/keyevents":
                {
                    int since = 0;
                    int.TryParse(ctx.Request.QueryString["since"], out since);
                    ReplyJson(ctx, 200, _eventsProvider(since));
                    return;
                }
                case "/api/btdevices":
                    ReplyJson(ctx, 200, ListPairedBtDevices());
                    return;
                case "/api/capture/poll":
                    ReplyJson(ctx, 200, CaptureService.Poll());
                    return;
                case "/api/autostart":
                    ReplyJson(ctx, 200, new JsonObject { ["enabled"] = AutostartQuery() });
                    return;
                case "/":
                    ServeFile(ctx, "index.html");
                    return;
                default:
                    if (path.StartsWith('/')) ServeFile(ctx, path[1..]);
                    return;
            }
        }
        else if (req.HttpMethod == "POST" && path is "/api/config" or "/api/capture/start"
                 or "/api/autostart" or "/api/cable/install" or "/api/learn")
        {
            if (!Guard(ctx)) return;
            if (path == "/api/learn")
            {
                ServeLearn(ctx);
                return;
            }
            if (path == "/api/autostart")
            {
                if (ReadJsonBody(ctx) is not { } j0) return;
                var on = j0["enabled"]?.GetValue<bool>() ?? false;
                var okA = on ? AutostartCreate() : AutostartDelete();
                ReplyJson(ctx, 200, new JsonObject
                {
                    ["ok"] = okA,
                    ["enabled"] = AutostartQuery(),
                    ["msg"] = okA ? (on ? "开机自启已开启" : "开机自启已关闭") : "操作失败（需要管理员，已尝试弹 UAC）",
                });
                return;
            }
            if (path == "/api/capture/start")
            {
                CaptureService.Start();
                ReplyJson(ctx, 200, new JsonObject { ["ok"] = true, ["msg"] = "捕获中（15s），按一下键盘/滚轮/鼠标" });
                return;
            }
            if (path == "/api/cable/install")
            {
                // 虚拟声卡安装。★绝不同步等：HTTP 是单线程串行 Handle，一等十几秒，
                //   /api/state 的 2s 轮询和学习模式的 300ms 轮询全排队、控制台冻住。
                //   安装丢后台，本路由立即回话；UI 本来就每 2s 刷 cable 状态，装没装上
                //   它自己会看到。管理员直装；普通权限 runas 拉自身 --install-cable
                //  （一次 UAC，装完即退 —— 对齐 --inject-only 的哲学）。
                if (!Guard(ctx)) return;
                if (!CableSetup.Installed())
                {
                    if (IsAdmin())
                        _ = Task.Run(async () =>
                        {
                            var (ok, msg) = await CableSetup.InstallAsync(Program.LogAs("CABLE"));
                            Program.Log("CABLE", $"手动安装结果：{(ok ? "成功" : "失败")} {msg}");
                        });
                    else
                        _ = Task.Run(() => Program.RunSelfElevated("--install-cable", 120000));
                }
                ReplyJson(ctx, 200, new JsonObject
                {
                    ["ok"] = true,
                    ["installed"] = CableSetup.Installed(),
                    ["msg"] = "安装已开始（若弹 UAC 请点「是」）—— 状态约 10-30 秒后自动刷新",
                });
                return;
            }
            if (ReadJsonBody(ctx) is not { } patch) return;
            var (ok, msg) = _config.Apply(patch);
            ReplyJson(ctx, 200, new JsonObject { ["ok"] = ok, ["msg"] = msg });
            return;
        }
        ctx.Response.StatusCode = 404;
        ReplyJson(ctx, 404, new JsonObject { ["ok"] = false, ["msg"] = "not found" });
    }

    /// <summary>读 POST body 并解析为 JsonObject（含 1MB 上限检查）。
    /// 已回复错误（413/400）时返回 null —— 调用方直接 return。
    /// config 主路径 / autostart / learn 三处共用，上限与错误语义不分叉。</summary>
    private static JsonObject? ReadJsonBody(HttpListenerContext ctx)
    {
        string body;
        try
        {
            using var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            body = sr.ReadToEnd();
        }
        catch { body = ""; }
        if (body.Length > 1_000_000)
        {
            ReplyJson(ctx, 413, new JsonObject { ["ok"] = false, ["msg"] = "请求体过大（上限 1MB）" });
            return null;
        }
        try
        {
            if (JsonNode.Parse(body) is JsonObject o) return o;
        }
        catch { }
        ReplyJson(ctx, 400, new JsonObject { ["ok"] = false, ["msg"] = "JSON 解析失败" });
        return null;
    }

    /// <summary>学习模式控制：start/stop/next 切换学习状态；save 把指纹+已命名键
    /// 落进 devices[当前remote_addr]（增量合并，逐键保存互不冲掉）。
    /// ★ 指纹是服务端学习会话推导的（KeysRole.LearnProfile 权威来源），不经
    ///   前端回传 —— 服务端产物绕道浏览器一圈就成了不可信输入。</summary>
    private void ServeLearn(HttpListenerContext ctx)
    {
        if (_keys is null)
        {
            ReplyJson(ctx, 200, new JsonObject { ["ok"] = false, ["msg"] = "按键角色未就绪" });
            return;
        }
        if (ReadJsonBody(ctx) is not { } j) return;
        void ReplyLearn(JsonObject learn) =>
            ReplyJson(ctx, 200, new JsonObject { ["ok"] = true, ["learn"] = learn });
        switch (j["action"]?.GetValue<string>() ?? "")
        {
            case "start":
                ReplyLearn(_keys.LearnStart());
                return;
            case "stop":
                ReplyLearn(_keys.LearnStop());
                return;
            case "next":   // 确认一键并保存动作后：状态机回 waiting，继续学下一个键
                ReplyLearn(_keys.LearnNext());
                return;
            case "save":
            {
                var addr = _config.Get("remote_addr", "");
                if (addr.Length == 0)
                {
                    ReplyJson(ctx, 200, new JsonObject { ["ok"] = false, ["msg"] = "先在设置页选中遥控器地址" });
                    return;
                }
                var dev = new JsonObject();
                if (_keys.LearnProfile() is { } pp) dev["report"] = pp;
                if (j["labels"] is JsonObject ll) dev["labels"] = ll.DeepClone();
                if (dev.Count == 0)
                {
                    ReplyJson(ctx, 200, new JsonObject { ["ok"] = false, ["msg"] = "没有可保存的内容" });
                    return;
                }
                var (okD, msgD) = _config.SetDevice(addr, dev);
                ReplyJson(ctx, 200, new JsonObject
                {
                    ["ok"] = okD,
                    ["msg"] = okD ? "已存入设备档案（指纹+键名）" : msgD,
                });
                return;
            }
            default:
                ReplyJson(ctx, 200, new JsonObject { ["ok"] = false, ["msg"] = "未知 action" });
                return;
        }
    }

    // ---------- 计划任务 / 开机自启（任务名唯一出处：登录触发 + 最高权限）----------
    internal const string TaskName = "VibeMate";

    /// <summary>经计划任务拉起本程序（/Run；任务=最高权限 → 起来就是管理员，无 UAC）。
    /// 首启让位（Program）与托盘重启兜底都走这里 —— schtasks 参数只写一份。</summary>
    internal static void RunViaTask()
    {
        Process.Start(new ProcessStartInfo
        { FileName = "schtasks", Arguments = $"/Run /TN {TaskName}",
          CreateNoWindow = true, UseShellExecute = false });
    }

    private static int RunSchtasks(string args, bool runas)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "schtasks",
            Arguments = args,
            CreateNoWindow = true,
            UseShellExecute = runas,
            Verb = runas ? "runas" : "",
        };
        try
        {
            using var p = Process.Start(psi)!;
            if (!p.WaitForExit(15000)) return -1;
            return p.ExitCode;
        }
        catch { return -1; }
    }

    internal static bool IsAdmin() => new System.Security.Principal.WindowsPrincipal(
        System.Security.Principal.WindowsIdentity.GetCurrent())
        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

    internal static bool AutostartQuery()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks", Arguments = $"/Query /TN {TaskName}",
                CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardError = true, RedirectStandardOutput = true,
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    internal static bool AutostartCreate()
    {
        if (AutostartQuery()) return true;
        var exe = Environment.ProcessPath ?? "";
        var args = $"/Create /F /TN {TaskName} /TR \"{exe}\" /SC ONLOGON /RL HIGHEST";
        var rc = IsAdmin() ? RunSchtasks(args, false) : RunSchtasks(args, true);
        return rc == 0 && AutostartQuery();
    }

    /// <summary>强制重建计划任务（--setup-task 子进程用；调用方已在提权上下文）。
    /// 与 AutostartCreate 的区别：不走「已存在即返回」—— 那会放过指向错误路径的
    /// 僵尸任务（deploy.ps1 靠它纠偏）。</summary>
    internal static bool RecreateAutostart()
    {
        RunSchtasks($"/Delete /F /TN {TaskName}", false);      // 不存在时失败无害
        var exe = Environment.ProcessPath ?? "";
        var rc = RunSchtasks($"/Create /F /TN {TaskName} /TR \"{exe}\" /SC ONLOGON /RL HIGHEST", false);
        return rc == 0 && AutostartQuery();
    }

    private static bool AutostartDelete()
    {
        if (!AutostartQuery()) return true;
        var rc = IsAdmin() ? RunSchtasks($"/Delete /F /TN {TaskName}", false)
                           : RunSchtasks($"/Delete /F /TN {TaskName}", true);
        return rc == 0 && !AutostartQuery();
    }

    /// <summary>v1 的两道闸：Origin/Referer 白名单 + 必须 application/json。</summary>
    private static bool Guard(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var origin = req.Headers["Origin"] ?? req.Headers["Referer"] ?? "";
        var host = req.Headers["Host"] ?? "";
        if (origin.Length > 0 && !origin.Contains(host) )
        {
            ReplyJson(ctx, 403, new JsonObject { ["ok"] = false, ["msg"] = "跨站请求被拒绝" });
            return false;
        }
        var ct = req.ContentType ?? "";
        if (!ct.Contains("application/json"))
        {
            ReplyJson(ctx, 415, new JsonObject { ["ok"] = false, ["msg"] = "必须 application/json" });
            return false;
        }
        return true;
    }

    private static void ReplyJson(HttpListenerContext ctx, int status, JsonNode payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString(ConfigService.JsonOpts));
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.Headers["Cache-Control"] = "no-store";
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.Close();
    }

    /// <summary>
    /// 枚举已配对的蓝牙设备（名称 + MAC）—— 设置页的 remote_addr 下拉数据源。
    /// 注册表法。实测形态：BTHLEDevice 下是「{服务GUID}_Dev_VID&xx_PID&xx_REV&xx_MAC」
    /// 的服务节点（同一设备 8 个服务 = 8 个键，按尾部 MAC 去重聚合）；
    /// BTHENUM（经典蓝牙，耳机/音箱在这）下是「Dev_MAC&容器」形态 —— 刻意不枚举：
    /// 经典设备没有 GATT，语音链路连不上，混进下拉纯属噪音。
    /// ★ 设备名：优先 BTHPORT\Parameters\Devices\<MAC> 的 Name 值（蓝牙设置页
    ///   显示的名字，如「Chromecast Remote」）；服务实例的 FriendlyName 兜底。
    /// </summary>
    private static JsonNode ListPairedBtDevices()
    {
        var arr = new JsonArray();
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // 1) 蓝牙栈的设备名表（最准）
            using (var nameRoot = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                       @"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices"))
            {
                if (nameRoot is not null)
                    foreach (var mac12 in nameRoot.GetSubKeyNames())
                    {
                        if (mac12.Length != 12 || !mac12.All(char.IsLetterOrDigit)) continue;
                        using var k = nameRoot.OpenSubKey(mac12);
                        var n = k?.GetValue("Name");
                        // ★ 实测是 REG_BINARY（ASCII/UTF-8 字节 + 尾部 \0），不是 REG_SZ
                        var s = n switch
                        {
                            string str => str,
                            byte[] bytes => Encoding.UTF8.GetString(bytes).TrimEnd('\0'),
                            _ => "",
                        };
                        if (s.Length > 0)
                            names[mac12.ToUpperInvariant()] = s;
                    }
            }

            // ★ 只枚举 BTHLEDevice（BLE GATT）：只有 BLE 设备才会在它下面建 GATT
            //   服务节点。BTHPORT 名字表是全量（经典+BLE），单用它会把配对过的
            //   耳机/手机全列进来 —— 用 bleMacs 白名单把没进 BTHLEDevice 的滤掉。
            // ★ BTHLEDevice 是唯一上榜依据（BLE GATT 设备才建服务节点）；同设备
            //   多服务节点按 MAC 聚合，vid/pid 只记首次。BTHPORT 名字表只是名字
            //   的来源（含经典蓝牙，不决定谁上榜）—— 三集合制衡反转成单一权威源。
            var devices = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
            using (var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                       @"SYSTEM\CurrentControlSet\Enum\BTHLEDevice"))
            {
                if (root is not null)
                    foreach (var node in root.GetSubKeyNames())
                    {
                        var key = KeysRole.BthleMacOf(node);
                        if (key is null || devices.ContainsKey(key)) continue;
                        var name = names.GetValueOrDefault(key, "");
                        if (name.Length == 0)
                        {
                            using var inst = root.OpenSubKey(node);
                            if (inst is not null)
                                foreach (var sub in inst.GetSubKeyNames())
                                {
                                    using var k = inst.OpenSubKey(sub);
                                    if (k?.GetValue("FriendlyName") is string s && s.Length > 0) { name = s; break; }
                                }
                        }
                        var mac = string.Join(':', Enumerable.Range(0, 6)
                            .Select(i => key.Substring(i * 2, 2)));
                        var item = new JsonObject { ["name"] = name, ["addr"] = mac };
                        if (KeysRole.BthleVidPidOf(node) is { } vp)
                        {
                            item["vid"] = vp.Vid;
                            item["pid"] = vp.Pid;
                            // 已知设备：vid+pid+蓝牙名三重认定（普通版与 Pro 共用
                            // VID/PID，名称不中不算）→ 预填型号 + 语音能力
                            // （免学习的便利层；未命中走学习）
                            if (DeviceDb.Lookup(vp.Vid, vp.Pid, name) is { } known)
                            {
                                item["model"] = known["model"]?.GetValue<string>();
                                item["voice"] = known["voice"]?.GetValue<string>();
                            }
                        }
                        devices[key] = item;
                        arr.Add(item);
                    }
            }
        }
        catch (Exception e)
        {
            return new JsonObject { ["ok"] = false, ["msg"] = $"{e.GetType().Name}: {e.Message}" };
        }
        return new JsonObject { ["ok"] = true, ["devices"] = arr };
    }

    /// <summary>日志尾部 N 行（默认 200，上限 1000）。排障第一入口（对齐 v1 纪律）。</summary>
    private void ServeLog(HttpListenerContext ctx)
    {
        var q = ctx.Request.QueryString;
        int n = 200;
        int.TryParse(q["n"], out n);
        n = Math.Clamp(n, 1, 1000);
        string text = "(日志文件还不存在)";
        try
        {
            if (File.Exists(_logPath))
            {
                var lines = File.ReadAllLines(_logPath);
                text = lines.Length <= n
                    ? string.Join(Environment.NewLine, lines)
                    : string.Join(Environment.NewLine, lines[^n..]);
            }
        }
        catch (Exception e) { text = $"(读取失败：{e.Message})"; }
        var bytes = Encoding.UTF8.GetBytes(text);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        ctx.Response.Headers["Cache-Control"] = "no-store";
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.Close();
    }

    // 嵌入资源缓存（web 页面打进 exe，内存直接吐 —— 用户改不了、删不掉）
    private static readonly Dictionary<string, (byte[] data, string ctype)> _resCache = new();

    private void ServeFile(HttpListenerContext ctx, string rel)
    {
        if (rel.Contains("..") || Path.IsPathRooted(rel))
        {
            ctx.Response.StatusCode = 403;
            ctx.Response.Close();
            return;
        }
        if (!_resCache.TryGetValue(rel, out var res))
        {
            var name = "VibeMate.web." + rel.Replace('/', '.');
            using var st = typeof(HttpServer).Assembly.GetManifestResourceStream(name);
            if (st is null)
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }
            using var ms = new MemoryStream();
            st.CopyTo(ms);
            var ext = Path.GetExtension(rel).ToLowerInvariant();
            res = (ms.ToArray(), ext switch
            {
                ".html" => "text/html; charset=utf-8",
                ".js" => "text/javascript; charset=utf-8",
                ".css" => "text/css; charset=utf-8",
                ".svg" => "image/svg+xml",
                ".png" => "image/png",
                _ => "application/octet-stream",
            });
            _resCache[rel] = res;
        }
        ctx.Response.ContentType = res.ctype;
        ctx.Response.Headers["Cache-Control"] = "no-store";
        ctx.Response.OutputStream.Write(res.data, 0, res.data.Length);
        ctx.Response.Close();
    }
}
