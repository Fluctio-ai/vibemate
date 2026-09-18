using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace VibeMate;

/// <summary>
/// VB-CABLE 静默卸载 —— v1 uninstall.py 的 cable/cable_residue 移植（C# 化）。
///
/// v2 安装走 pnputil + SwDeviceCreate 静默路，官方 GUI 卸载器（要人在窗口里点
/// 「Remove Driver」+ 卸完必须重启）与自动化哲学不合 —— 所以按 v1 验证过的
/// 残留清单做**静默全卸**（那套清单本来就是「官方卸载器永远不碰的部分」，
/// 做全了恰好就是完整卸载）：
///   1. pnputil /delete-driver /uninstall /force（先 /enum-drivers 找包，设备
///      节点随 /uninstall 一并移除；发布名 oemNN.inf 删不动再试原始名）；
///   2. 服务键 VB-Cable / VBAudioVACMME：★先 stop 再 delete —— 直接 delete 会留
///      「待删除」影子（SCM 1072 状态），把下一次安装整个挡住；
///   3. HKLM\SOFTWARE\VB-Audio\Cable —— Voicemeeter 也挂在厂商键下面（Banana/
///      Potato），只删 Cable 这一支，兄弟键一律不碰，删空才顺手删壳；
///   4. 「应用和功能」卸载项（官方安装器留的；v2 静默装不会产生，官方装过则有）；
///   5. C:\Program Files\VB\CABLE —— 仅当内容恰好是官方三件时删，多一个文件不动。
/// 安全底线（v1 血泪：清注册表最怕多删一位）：所有可删路径白名单写死在本文件，
/// 不从任何外部输入拼；服务键删前验 ImagePath（见 GuardService）。
/// </summary>
internal static class CableUninstall
{
    // 官方注册的两个服务名（探测依据，v1 同款 —— 不看音频端点，端点在卸载中消失
    // 是预期而非「没装」）
    private static readonly string[] SvcNames = { "VB-Cable", "VBAudioVACMME" };
    private const string SvcRoot = @"SYSTEM\CurrentControlSet\Services";
    private const string VendorKey = @"SOFTWARE\VB-Audio";
    private const string VendorSubKey = "Cable";
    private const string UninstRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private static readonly string ProgFilesDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VB");
    private static readonly string CableFilesDir = Path.Combine(ProgFilesDir, "CABLE");
    // C:\Program Files\VB\CABLE 里官方就这三个文件；多出一个 = 不是我们认识的
    // VB-CABLE 安装，宁可不动（v1 纪律）
    private static readonly HashSet<string> KnownFiles = new(StringComparer.OrdinalIgnoreCase)
    { "VBCABLE_Setup_x64.exe", "VBCABLE_ControlPanel.exe", "vbMmeCable64_win10.inf" };

    private static int _busy;

    // ---- 卸载进度文件：主进程直卸（管理员）与提权子进程（--uninstall-cable）的
    //      唯一进度通道 —— 子进程没法回话，v1 的 _uninstall_result.json 同款套路。
    //      ProgramData 是双方都能算出的公共位置（对齐 DriverDir），不随 exe 换目录漂移。
    private static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "VibeMate", "vbcable", "uninstall_state.json");

    /// <summary>单步进度：name + status(pending/running/done/warn/skip) + note。
    /// 界面照着渲染成 ✓/△/⏳ 清单 —— 用户点名要逐步可见，别只给一行结论。</summary>
    private sealed class StepState
    {
        public string Name = "", Status = "pending", Note = "";
    }

    /// <summary>写进度文件。写失败静默 —— 进度只是 UI 便利，绝不能拖垮卸载本身。</summary>
    private static void WriteSteps(string phase, List<StepState> steps, string msg, bool reboot)
    {
        try
        {
            var arr = new JsonArray();
            foreach (var s in steps)
                arr.Add(new JsonObject { ["name"] = s.Name, ["status"] = s.Status, ["note"] = s.Note });
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, new JsonObject
            {
                ["phase"] = phase,
                ["steps"] = arr,
                ["msg"] = msg,
                ["reboot"] = reboot,
                ["time"] = DateTimeOffset.Now.ToString("HH:mm:ss"),
            }.ToJsonString(ConfigService.JsonOpts));
        }
        catch { }
    }

    // mtime 缓存：/api/state 每 2s 一问，文件没变不重读重解析。★ 对外只给
    // DeepClone —— JsonNode 有父引用，同一实例塞进第二个 state 对象会抛
    //「already has a parent」把 stateBuilder 炸成 500（实测教训），正本不出库
    private static JsonObject? _snapCache;
    private static DateTime _snapMtime;

    /// <summary>进度快照（/api/state.cable.uninstall 数据源）。null = 无卸载可报。
    /// 清理时机（只在非 running 时）：
    ///   · 端点已消失（reboot=true 的场景 = 用户重启过）→ 提醒使命完成，删文件；
    ///   · running 但 10 分钟没更新 → 子进程死了，丢弃（下次卸载会重写）。
    /// 返回的 reboot=true 就是「重启提醒」的持久来源 —— 前端据此挂横幅直到端点消失。</summary>
    internal static JsonObject? SnapshotUninstall()
    {
        try
        {
            if (!File.Exists(StatePath)) { _snapCache = null; return null; }
            var mtime = File.GetLastWriteTimeUtc(StatePath);
            if (_snapCache is not null && mtime == _snapMtime) return (JsonObject)_snapCache.DeepClone();
            _snapCache = null;
            if (JsonNode.Parse(File.ReadAllText(StatePath)) is not JsonObject o) return null;
            var phase = o["phase"]?.GetValue<string>() ?? "";
            if (phase == "running")
            {
                if ((DateTime.UtcNow - mtime).TotalMinutes > 10)
                { try { File.Delete(StatePath); } catch { } return null; }
                _snapMtime = mtime; _snapCache = o;
                return (JsonObject)o.DeepClone();
            }
            // 到这一步安装缓存必是热的（stateBuilder 刚问过 Installed），不产生额外枚举
            if (!CableSetup.Installed())
            { try { File.Delete(StatePath); } catch { } return null; }
            _snapMtime = mtime; _snapCache = o;
            return (JsonObject)o.DeepClone();
        }
        catch { return null; }
    }

    /// <summary>卸载（要求管理员；普通权限由调用方 runas 拉自身 --uninstall-cable）。
    /// 并发防护对齐 InstallAsync：HTTP 重放 + 提权子进程不会同时进来。</summary>
    public static async Task<(bool Ok, string Msg)> UninstallAsync(Func<string, Task> log)
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0)
            return (true, "卸载已在进行中");
        try { return await UninstallCore(log); }
        finally { _busy = 0; }
    }

    private static async Task<(bool Ok, string Msg)> UninstallCore(Func<string, Task> log)
    {
        var rebootAdvised = false;

        // 步骤清单（与下方执行顺序一一对应；SetStep 即时落进度文件 → 界面清单）
        var steps = new List<StepState>
        {
            new() { Name = "驱动包", Status = "running", Note = "pnputil 查找并删除" },
            new() { Name = "服务键", Note = "VB-Cable / VBAudioVACMME" },
            new() { Name = "注册表", Note = @"VB-Audio\Cable 等" },
            new() { Name = "卸载项", Note = "「应用和功能」里的条目" },
            new() { Name = "程序目录", Note = @"C:\Program Files\VB" },
            new() { Name = "端点确认", Note = "CABLE Input 是否消失" },
        };
        void SetStep(int i, string status, string note)
        {
            steps[i].Status = status;
            steps[i].Note = note;
            WriteSteps("running", steps, "", rebootAdvised);
        }
        WriteSteps("running", steps, "", false);

        // ---- 1) 驱动包（root 设备节点随 /uninstall 移除）----
        var drivers = await EnumDriverPackagesAsync(log);
        var driverRemoved = drivers.Count == 0;          // 没找到 = 无此步可做，不算失败
        var drvNote = drivers.Count == 0 ? "未找到（本机未走驱动包路安装）" : "";
        foreach (var name in drivers)                    // 发布名（oemNN.inf）排前：pnputil 只认它
        {
            var (rc, outText) = await RunAsync("pnputil",
                $"/delete-driver {name} /uninstall /force", 180_000);
            // ★ 3010 = ERROR_SUCCESS_REBOOT_REQUIRED：删除已受理、重启才生效 ——
            //   驱动被占用（常见：本程序自己的语音出口正抓着 CABLE Input，内核驱动
            //   NOT_STOPPABLE）时必走这条。按「删不掉」处理是错的（v2.1.4 实测：
            //   误试原始名 + 最终误报失败），它是成功，只是要重启。
            if (rc == 0 || rc == 3010)
            {
                if (rc == 3010)
                { await log($"驱动包 {name} 删除已受理 —— 需重启完成（pnputil 3010）"); rebootAdvised = true; }
                else await log($"驱动包已删（{name}）");
                drvNote = rc == 3010 ? $"{name} 已受理，需重启完成" : $"已删 {name}";
                driverRemoved = true; break;
            }
            await log($"驱动包 {name} 删不掉（{Tail(outText)}），试另一个名字");
        }
        if (!driverRemoved) rebootAdvised = true;        // 包还挂在 DriverStore：重装前必须清掉
        if (drvNote.Length == 0) drvNote = "删不掉（详见日志），重启后可清";
        SetStep(0, driverRemoved ? (rebootAdvised ? "warn" : "done") : "warn", drvNote);

        // ---- 2) 服务键：先停再删 ----
        SetStep(1, "running", "先停后删（防 1072 待删除影子）");
        var svcDone = 0; var svcSkip = 0;
        foreach (var svc in SvcNames)
        {
            var keyPath = $@"{SvcRoot}\{svc}";
            string why;
            using (var k = Registry.LocalMachine.OpenSubKey(keyPath))
            {
                if (k is null)
                {
                    // 键没了但 SCM 可能还挂着「待删除」影子（重启才消）—— 报给用户
                    if (await ScKnowsServiceAsync(svc))
                    { await log($"SCM 里还留着 {svc}（注册表键已删）—— 重启一次才会消失"); rebootAdvised = true; }
                    continue;
                }
                var (allow, w) = GuardService(k, svc);
                if (!allow) { await log($"跳过服务 {svc}：{w}"); svcSkip++; continue; }
                why = w;
            }
            await RunAsync("sc", $"stop {svc}", 90_000);          // 没在跑时报 1062，无害
            await Task.Delay(1000);                               // 给驱动卸载留一拍
            await RunAsync("sc", $"delete {svc}", 60_000);
            if (Registry.LocalMachine.OpenSubKey(keyPath) is not null)
            {
                // 孤儿键：SCM 不认（sc delete 删不掉），直接删注册表
                try { Registry.LocalMachine.DeleteSubKeyTree(keyPath); }
                catch (Exception e) { await log($"服务键 {svc} 直接删失败：{e.Message}"); }
            }
            await log($"服务键 {svc} 已删（{why}）");
            svcDone++;
        }
        SetStep(1, "done", svcSkip > 0 ? $"已删 {svcDone}、跳过 {svcSkip}（见日志）" : "已删 ×2");

        // ---- 3) 厂商键：只删 Cable 一支 ----
        SetStep(2, "running", "只删 Cable 一支，Voicemeeter 不碰");
        try
        {
            string[] subs;
            using (var vk = Registry.LocalMachine.OpenSubKey(VendorKey))
            {
                if (vk is null) subs = Array.Empty<string>();
                else subs = vk.GetSubKeyNames();
            }
            if (subs.Contains(VendorSubKey, StringComparer.OrdinalIgnoreCase))
            {
                Registry.LocalMachine.DeleteSubKeyTree($@"{VendorKey}\{VendorSubKey}");
                await log($@"已删 HKLM\SOFTWARE\VB-Audio\Cable（只有 CABLE 自己的设置）");
            }
            var siblings = subs.Where(s => !s.Equals(VendorSubKey, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (siblings.Length > 0)
                await log($@"VB-Audio 下还有 {string.Join("、", siblings)}（Voicemeeter 之类），一律不动");
            else if (Registry.LocalMachine.OpenSubKey(VendorKey) is not null)
            {
                // 能走到这 = 厂商键要么刚删空、要么本来就只剩值 —— DeleteSubKey(false)
                // 只删无子键的键，真有意外子键会抛给下面的 catch，不会误删
                Registry.LocalMachine.DeleteSubKey(VendorKey, false);
                await log(@"HKLM\SOFTWARE\VB-Audio 已空，顺手删掉空壳键");
            }
        }
        catch (Exception e) { await log($"厂商键清理异常（继续）：{e.Message}"); }
        SetStep(2, "done", "已清理");

        // ---- 4) 「应用和功能」卸载项 ----
        // ★ 必须见到 vb-audio（Publisher=VB-Audio Software）才算数：DisplayName 里
        //   带 vbcable 字样的第三方条目完全可能存在，只看名字会删别人的卸载项
        SetStep(3, "running", "校验厂商后删除");
        var uninstDeleted = 0;
        try
        {
            string[] subs = Array.Empty<string>();
            using (var root = Registry.LocalMachine.OpenSubKey(UninstRoot))
                if (root is not null) subs = root.GetSubKeyNames();
            foreach (var sub in subs)
            {
                if (!sub.ToLowerInvariant().Replace(" ", "").Contains("vbcable")) continue;
                string blob = "";
                using (var sk = Registry.LocalMachine.OpenSubKey($@"{UninstRoot}\{sub}"))
                {
                    if (sk is null) continue;
                    blob = $"{sk.GetValue("Publisher")} {sk.GetValue("DisplayName")}"
                           .ToLowerInvariant().Replace(" ", "");
                }
                if (!blob.Contains("vb-audio")) continue;
                Registry.LocalMachine.DeleteSubKeyTree($@"{UninstRoot}\{sub}");
                await log($"已删「应用和功能」卸载项：{sub}");
                uninstDeleted++;
            }
        }
        catch (Exception e) { await log($"卸载项清理异常（继续）：{e.Message}"); }
        SetStep(3, "done", uninstDeleted > 0 ? $"已删 {uninstDeleted} 条" : "无（静默安装不产生）");

        // ---- 5) 程序目录：只认官方那三个文件 ----
        SetStep(4, "running", "仅当内容恰为官方三件");
        var dirNote = "不存在";
        try
        {
            if (Directory.Exists(CableFilesDir))
            {
                var files = Directory.GetFiles(CableFilesDir).Select(Path.GetFileName)
                                     .Where(f => f is not null).Cast<string>().ToArray();
                if (files.Length > 0 && files.All(KnownFiles.Contains))
                {
                    Directory.Delete(CableFilesDir, true);
                    await log($@"已删 {CableFilesDir}（里面恰好是官方那三个文件）");
                    dirNote = "已删";
                }
                else if (files.Length > 0)
                {
                    await log($"{CableFilesDir} 里有别的文件（{string.Join("、", files.Take(3))}），不动");
                    dirNote = "内容非官方三件，未动";
                }
            }
            if (Directory.Exists(ProgFilesDir)
                && !Directory.EnumerateFileSystemEntries(ProgFilesDir).Any())
            {
                Directory.Delete(ProgFilesDir);
                await log(@"C:\Program Files\VB 已空，删掉");
            }
        }
        catch (Exception e) { await log($"程序目录清理异常（继续）：{e.Message}"); dirNote = "清理异常（见日志）"; }
        SetStep(4, "done", dirNote);

        // ---- 收尾：端点真消失了才算卸完（PnP 异步，对齐安装侧的轮询纪律）----
        SetStep(5, "running", "轮询端点（最长 12s）");
        for (int i = 0; i < 12; i++)
        {
            CableSetup.Invalidate();
            if (!CableSetup.Installed()) break;
            await Task.Delay(1000);
        }
        CableSetup.Invalidate();
        if (!CableSetup.Installed())
        {
            steps[5].Status = "done";
            steps[5].Note = "端点已消失";
            var msg = rebootAdvised
                ? "已卸载 —— 建议重启一次系统（残留的待删除服务/驱动包重启后才彻底消失，也避免下次安装报错）"
                : "已卸载";
            WriteSteps("done", steps, msg, rebootAdvised);
            await log(msg);
            return (true, msg);
        }
        // 端点仍在 + 删除已受理（3010 待重启）＝卸载进行中而非失败 —— 内核驱动
        // 卸载要重启，端点跟着驱动一起消失，这条必须报成功，否则用户会对着
        // 「失败」字样疑惑（v2.1.4 实测踩坑）
        if (rebootAdvised && driverRemoved)
        {
            steps[5].Status = "warn";
            steps[5].Note = "仍在（内核驱动占用，重启后消失）";
            var accepted = "卸载已受理 —— 重启一次系统后设备端点消失、卸载彻底完成";
            WriteSteps("done", steps, accepted, true);
            await log(accepted);
            return (true, accepted);
        }
        var fail = "没卸干净（端点仍在 —— 多半驱动被占用）：重启一次系统即可彻底移除";
        steps[5].Status = "warn";
        steps[5].Note = "仍在（见日志）";
        WriteSteps("failed", steps, fail, rebootAdvised);
        await log(fail);
        return (false, fail);
    }

    /// <summary>从 pnputil /enum-drivers 里挑 VB-CABLE 的驱动包名（.inf，两种写法都收）。
    /// ★ 输出是本地化的（中文系统打「发布名称」），只能抓 .inf token，按标签解析必炸；
    /// ★ 判据必须带 vbmmecable / vbaudio_cable 专属字样 —— Voicemeeter 的 Provider
    ///   也是 VB-Audio，按厂商匹配会删掉人家的驱动包。</summary>
    private static async Task<List<string>> EnumDriverPackagesAsync(Func<string, Task> log)
    {
        var (_, outText) = await RunAsync("pnputil", "/enum-drivers", 90_000);
        var names = new List<string>();
        // ★ 分块容忍 CRLF（split("\n\n") 切不开 CRLF 文本，会把全机几百个驱动包
        //   当成一个块误收 —— v1 实测灾难）；配数量上限双保险
        foreach (var blk in Regex.Split(outText ?? "", @"\r?\n\s*\r?\n"))
        {
            var low = blk.ToLowerInvariant();
            if (!low.Contains("vbmmecable") && !low.Contains("vbaudio_cable")) continue;
            foreach (Match m in Regex.Matches(blk, @"[^\s\\/:]+\.inf", RegexOptions.IgnoreCase))
                names.Add(m.Value.Trim());
        }
        var uniq = names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (uniq.Count > 4)          // 正常只有 1 个包：超限 = 解析出错，宁可不删不乱删
        {
            await log($"enum-drivers 匹配到 {uniq.Count} 个包，判定解析异常 —— 驱动包一步跳过");
            return new List<string>();
        }
        uniq.Sort((a, b) => (a.StartsWith("oem", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                           .CompareTo(b.StartsWith("oem", StringComparison.OrdinalIgnoreCase) ? 0 : 1));
        return uniq;
    }

    /// <summary>删服务键前的正身核验（v1 _cable_svc_guard 移植）。
    /// 有 Voicemeeter 字样 / ImagePath 不指向 CABLE 驱动 → 拒删；ImagePath 为空
    /// （半卸后 SCM 都不认的孤儿键）→ 放行。</summary>
    private static (bool Allow, string Why) GuardService(RegistryKey svcKey, string name)
    {
        var blob = "";
        var image = "";
        foreach (var v in new[] { "ImagePath", "DisplayName", "Description" })
        {
            var s = svcKey.GetValue(v) as string ?? "";
            blob += " " + s;
            if (v == "ImagePath") image = s.Trim();
        }
        if (blob.ToLowerInvariant().Contains("voicemeeter"))
            return (false, "里面有 Voicemeeter 字样，是别人家的声卡");
        if (image.Length == 0) return (true, "孤儿服务键（ImagePath 为空、SCM 已不认它）");
        var low = image.ToLowerInvariant();
        if (low.Contains("vbaudio_cable64") || low.Contains("vbmmecable64"))
            return (true, "ImagePath 指向 VB-CABLE 驱动");
        return (false, $"ImagePath 不指向 VB-CABLE 驱动（{image[..Math.Min(40, image.Length)]}）");
    }

    /// <summary>SCM 还认不认这个服务（sc query 出任何状态 = 认；键删了它还认 =
    /// 「待删除」影子，只能重启清掉）。</summary>
    private static async Task<bool> ScKnowsServiceAsync(string svc)
    {
        var (rc, outText) = await RunAsync("sc", $"query {svc}", 60_000);
        return rc == 0 && (outText ?? "").Length > 0;
    }

    /// <summary>子进程跑完拿 (退出码, 输出)。两个流并发读 —— 顺序读会在另一个流
    /// 写满管道时死锁（v1 用 capture_output 单流无此坑，C# 得自己摆平）。</summary>
    private static async Task<(int, string)> RunAsync(string file, string args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = file, Arguments = args,
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            var tOut = p.StandardOutput.ReadToEndAsync();
            var tErr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            { try { p.Kill(); } catch { } return (-1, "timeout"); }
            return (p.ExitCode, await tOut + await tErr);
        }
        catch (Exception e) { return (-1, e.Message); }
    }

    private static string Tail(string s)
    {
        var lines = (s ?? "").Trim().Split('\n');
        return lines.Length > 0 ? lines[^1].Trim() : "?";
    }
}
