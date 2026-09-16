/* tap.c — VibeMote v2 按键旁路 DLL（替代 frida + tap.js，逻辑一比一翻译）
 *
 * 挂进 WUDFHost.exe（蓝牙 HID 驱动宿主）后只做三件事：
 *   1. 钩 ntdll!NtDeviceIoControlFile：IOCTL 0x80018483 完成时，输出缓冲区
 *      就是 GATT 读上来的 HID 输入报告。抄 3 字节遥控器报告给主程序；
 *   2. 把「已被映射」的 usage 原地清零 —— 驱动以为没按键，原生动作消失
 *      （v1 tap.js 的 nullify，消灭原生+合成双发）；
 *   3. 管道协议 + 生命周期：防重复注入、退出注销。
 *
 * 管道：\\.\pipe\vibemote-keys-<本进程PID>（DLL=server，主程序=client）
 * 协议（行式，\n 结尾，ASCII）：
 *   DLL → APP:  report <len> <b0> <b1> ...    内容有变化才发（len=字节数，1-64）
 *               hb <total> <sent> <blocked>   心跳 20s
 *               ack-block <n>
 *   APP → DLL:  filter <len> <id>             报告指纹（len=0 → 学习模式全量上报）
 *               block @<off>:<w> <usage...>   在偏移 off 处按 w 字节 LE 清位
 *               ping                          立即回 hb
 *               unload                        注销钩子并自卸
 *
 * ★ 指纹（filter 的长度/ReportID、block 的偏移/宽度）全部由主程序按设备下发，
 *   DLL 不内置任何型号知识 —— 支持新遥控器不需要改这里。默认值 = Google
 *   遥控器（3 字节 / id 0x02 / usage 在 b[1..2] LE），只用于「主程序下发前」
 *   的向后兼容。学习模式（filter 0）全量上报且绝不清位（同一宿主还有别的
 *   蓝牙键鼠，清位会弄坏它们）。
 *
 * 生命周期（★ 用户验收点）：
 *   · 防重复注入：CreateNamedPipe 带 FILE_FLAG_FIRST_PIPE_INSTANCE，
 *     管道名被占（已有一份）→ 这一份立即自卸退出；
 *   · 退出注销：收到 unload，或管道断开/写失败（主程序没了）→
 *     MH_DisableHook → 宽限 500ms（在飞调用出清）→ MH_Uninitialize →
 *     FreeLibraryAndExitThread。绝不留僵尸钩子（v1 的事故教训）。
 */

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <sddl.h>
#include <stdio.h>
#include <stdlib.h>
#include "MinHook.h"

/* 诊断日志（排障用；正式版可保留——文件小、只 append） */
static CRITICAL_SECTION g_log_cs;
static void logf(const char *fmt, ...)
{
    char path[MAX_PATH], line[256];
    GetWindowsDirectoryA(path, MAX_PATH);
    lstrcatA(path, "\\Temp\\tap_debug.log");
    va_list ap; va_start(ap, fmt);
    wvsprintfA(line, fmt, ap);
    va_end(ap);
    EnterCriticalSection(&g_log_cs);
    HANDLE f = CreateFileA(path, FILE_APPEND_DATA, FILE_SHARE_READ|FILE_SHARE_WRITE,
                           NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (f != INVALID_HANDLE_VALUE) {
        DWORD wr; WriteFile(f, line, lstrlenA(line), &wr, NULL);
        CloseHandle(f);
    }
    LeaveCriticalSection(&g_log_cs);
}

#define READ_IOCTL   0x80018483UL
#define HB_PERIOD_MS 20000

/* ---------------- 状态 ---------------- */
static HANDLE g_pipe = INVALID_HANDLE_VALUE;   /* 与主程序的连接 */
static HANDLE g_thread = NULL;
static HMODULE g_self = NULL;
static volatile LONG g_stopping = 0;           /* 卸载流程已启动 */
static volatile LONG g_pipe_ok = 0;            /* 管道已连通（可发送） */

static CRITICAL_SECTION g_send_cs;             /* 管道写互斥（hook线程/管道线程共用） */

static volatile LONG g_total = 0;              /* IOCTL 命中计数 */
static volatile LONG g_sent = 0;               /* 实际上报的 report 数 */
static volatile LONG g_blocked = 0;            /* 清位次数 */

#define MAX_BLOCK 64
static volatile USHORT g_block[MAX_BLOCK];     /* 清位表（usage 值） */
static volatile LONG g_block_n = 0;

/* 报告指纹（主程序下发；默认 Google 遥控器，见文件头协议注释） */
static volatile LONG g_flt_len = 3;            /* 匹配的报告长度；0 = 学习模式全量 */
static volatile LONG g_flt_id  = 0x02;         /* 匹配的 Report ID（b[0]） */
static volatile LONG g_blk_off = 1;            /* usage 槽起始偏移 */
static volatile LONG g_blk_w   = 2;            /* usage 宽度（1 或 2 字节，LE） */

#define MAX_RPT 64
static char g_last_hex[MAX_RPT * 3 + 8];       /* 上一份报告（长度+内容都变才发） */

/* ---------------- 钩子 ---------------- */
typedef LONG NTSTATUS;                          /* mingw 头里没带，自己定义 */
typedef NTSTATUS(NTAPI *PFN_NtDevIoCtl)(HANDLE, HANDLE, PVOID, PVOID, PVOID,
                                        ULONG, PVOID, ULONG, PVOID, ULONG);
static PFN_NtDevIoCtl g_orig = NULL;

static void send_line(const char *line)
{
    /* 在管道写锁内发送；未连接/停止中直接丢弃（计数照走） */
    if (!g_pipe_ok || g_stopping)
        return;
    EnterCriticalSection(&g_send_cs);
    if (g_pipe != INVALID_HANDLE_VALUE) {
        DWORD wr = 0;
        WriteFile(g_pipe, line, (DWORD)lstrlenA(line), &wr, NULL);
        /* 失败不重试：主程序侧断开由管道线程的 ReadFile 错误兜住 */
    }
    LeaveCriticalSection(&g_send_cs);
}

static void hexn(const BYTE *p, int n, char *out /* >= n*3+1 */)
{
    static const char hx[] = "0123456789abcdef";
    for (int i = 0; i < n; i++)
    {
        *out++ = hx[p[i] >> 4];
        *out++ = hx[p[i] & 15];
        *out++ = ' ';
    }
    out[-1] = '\0';                            /* 尾部空格换 \0 */
}

static int usage_blocked(USHORT u)
{
    LONG n = g_block_n;
    for (LONG i = 0; i < n; i++)
        if (g_block[i] == u)
            return 1;
    return 0;
}

static NTSTATUS NTAPI detour_NtDevIoCtl(HANDLE fh, HANDLE ev, PVOID apc, PVOID ctx,
                                        PVOID iosb, ULONG code,
                                        PVOID inbuf, ULONG inlen,
                                        PVOID outbuf, ULONG outlen)
{
    /* 参数全在本栈帧上：before/after 天然安全，无需 TLS。
       只有 IOCTL 命中才在返回后碰输出缓冲区（此时数据已填好）。 */
    const BOOL cap = (code == READ_IOCTL);
    NTSTATUS r = g_orig(fh, ev, apc, ctx, iosb, code, inbuf, inlen, outbuf, outlen);
    if (!cap || r != 0 || outbuf == NULL || outlen < 1 || outlen > MAX_RPT)
        return r;

    /* 指纹过滤：flen>0 = 常态（长度+ReportID 都对上才是遥控器报告）；
       flen==0 = 学习模式（全量上报，交给主程序按「按下/松开对比」推导指纹） */
    LONG flen = g_flt_len;
    BYTE *b = (BYTE *)outbuf;
    if (flen > 0 && ((LONG)outlen != flen || b[0] != (BYTE)g_flt_id))
        return r;
    InterlockedIncrement(&g_total);

    char hex[MAX_RPT * 3 + 8];
    int n = (int)outlen;
    hexn(b, n, hex);
    /* 内容变化才上报（空闲帧不刷屏）。长度一起进缓存：不同长度交替的流
       （学习模式下混着鼠标/键盘报告）不会互相误判为「变化」 */
    if (lstrcmpA(hex, g_last_hex) != 0)
    {
        char line[MAX_RPT * 3 + 32];
        wsprintfA(line, "report %d %s\n", n, hex);
        lstrcpyA(g_last_hex, hex);
        InterlockedIncrement(&g_sent);
        send_line(line);
    }

    /* 清位：已映射的 usage 槽位写 0 —— 必须在报告快照发出之后。
       学习模式（flen==0）绝不清位：全量流里混着同一宿主里其他蓝牙键鼠
       的报告，动了会把人家的输入弄坏。
       w 的合法域钳制在 apply_block 解析点做 —— 这里是每个蓝牙报告都走的
       热路径，只留随报告长度变化的越界检查。 */
    if (flen > 0)
    {
        LONG off = g_blk_off, w = g_blk_w;
        if (off < 0 || off + w > (LONG)outlen) return r;
        USHORT u = (USHORT)(w == 1 ? b[off] : (b[off] | (b[off + 1] << 8)));
        if (u && usage_blocked(u))
        {
            for (LONG i = 0; i < w; i++) b[off + i] = 0;
            InterlockedIncrement(&g_blocked);
        }
    }
    return r;
}

/* ---------------- 管道与生命周期 ---------------- */
static void send_hb(void)
{
    char line[64];
    wsprintfA(line, "hb %d %d %d\n", g_total, g_sent, g_blocked);
    send_line(line);
}

static void apply_filter(const char *args)
{
    /* filter <len> <id> —— 报告指纹；len=0 进入学习模式（全量上报，不清位） */
    char *end = NULL;
    unsigned long len = strtoul(args, &end, 10);
    while (*end == ' ') end++;
    char *end2 = NULL;
    unsigned long id = strtoul(end, &end2, 16);
    if (end2 == end) return;                   /* 参数不全：不动现有指纹 */
    InterlockedExchange(&g_flt_len, (LONG)len);
    InterlockedExchange(&g_flt_id, (LONG)id);
    char line[32];
    wsprintfA(line, "ack-filter %lu %lu\n", len, id);
    send_line(line);
}

static void apply_block(const char *args)
{
    /* 语法：block @<off>:<w> <usage>... —— 槽偏移/宽度由主程序按设备指纹下发。
       （不带 @ 前缀 = 旧语法，偏移沿用现值，兼容早期主程序） */
    if (*args == '@')
    {
        char *end = NULL;
        unsigned long off = strtoul(args + 1, &end, 10);
        unsigned long w = 2;
        if (*end == ':' && end[1] != '\0')
        {
            char *end2 = NULL;
            w = strtoul(end + 1, &end2, 10);
            if (end2 == end + 1) return;       /* 宽度解析失败：整条丢弃 */
            end = end2;
        }
        if (end == args + 1) return;
        InterlockedExchange(&g_blk_off, (LONG)off);
        if (w < 1) w = 1;                       /* 合法域钳制在解析点（唯一生产方） */
        if (w > 2) w = 2;
        InterlockedExchange(&g_blk_w, (LONG)w);
        while (*end == ' ') end++;
        args = end;
    }
    USHORT tmp[MAX_BLOCK];
    int n = 0;
    while (*args && n < MAX_BLOCK) {
        while (*args == ' ') args++;
        if (!*args) break;
        char *end = NULL;
        unsigned long v = strtoul(args, &end, 16);
        if (end == args) break;
        tmp[n++] = (USHORT)v;
        args = end;
    }
    /* 逐槽写入（hook 线程可能正在读；单写者=本线程，读侧容忍短暂新旧混合） */
    for (int i = 0; i < n; i++) g_block[i] = tmp[i];
    g_block_n = n;
    char line[32];
    wsprintfA(line, "ack-block %d\n", n);
    send_line(line);
}

static DWORD WINAPI death_timer(LPVOID arg)
{
    /* 重连宽限看护：客户端断开后 30s 内主程序会重连（会话重建很正常）；
       到点仍无连接（主程序真死了）→ 标记注销并「自连接」唤醒阻塞中的
       ConnectNamedPipe，让主线程走注销流程。 */
    char name[80];
    wsprintfA(name, "\\\\.\\pipe\\vibemote-keys-%lu", GetCurrentProcessId());
    Sleep(30000);
    if (!g_pipe_ok && !g_stopping) {
        g_stopping = 1;
        HANDLE h = CreateFileA(name, GENERIC_READ | GENERIC_WRITE, 0, NULL,
                               OPEN_EXISTING, 0, NULL);
        if (h != INVALID_HANDLE_VALUE) { Sleep(300); CloseHandle(h); }
    }
    return 0;
}

static DWORD WINAPI pipe_thread(LPVOID unused)
{
    (void)unused;
    char name[80];
    wsprintfA(name, "\\\\.\\pipe\\vibemote-keys-%lu", GetCurrentProcessId());

    /* ★ 防重复注入：FIRST_PIPE_INSTANCE —— 名字被占 = 本进程里已有一份 */
    /* 管道 DACL 放开（Everyone/管理员可读写）：WUDFHost 是服务账户，
       默认 DACL 会挡住普通权限的主程序连接。 */
    SECURITY_ATTRIBUTES sa, *psa = NULL;
    PSECURITY_DESCRIPTOR psd = NULL;
    if (ConvertStringSecurityDescriptorToSecurityDescriptorA(
            "D:P(A;;GA;;;WD)(A;;GA;;;BA)", SDDL_REVISION_1, &psd, NULL))
    {
        sa.nLength = sizeof(sa);
        sa.lpSecurityDescriptor = psd;
        sa.bInheritHandle = FALSE;
        psa = &sa;
    }
    g_pipe = CreateNamedPipeA(name,
                              PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE,
                              PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
                              1,                 /* 单实例 */
                              /* 出/入缓冲 16K：学习模式全量流（鼠标 ~125Hz 报告）下
                                 1K 缓冲几条就满，主程序读慢时 WriteFile 会阻塞在
                                 HID 投递路径本身 —— 缓冲大一点是保险 */
                              16384, 16384, 0, psa);
    if (psd) LocalFree(psd);
    if (g_pipe == INVALID_HANDLE_VALUE) {
        char buf[128];
        wsprintfA(buf, "[tap] CreateNamedPipe 失败 err=%lu —— 已有一份，自杀\n",
                  GetLastError());
        OutputDebugStringA(buf); logf(buf);
        FreeLibraryAndExitThread(g_self, 0);    /* 这一份自杀，先来的继续干活 */
        return 0;
    }
    logf("[tap] pipe created: %s (pid=%lu)\n", name, GetCurrentProcessId());

    /* 装 hook（在管道等待连接之前装好，早到的报告先进计数、连通后可见） */
    if (MH_Initialize() != MH_OK ||
        MH_CreateHookApi(L"ntdll.dll", "NtDeviceIoControlFile",
                         (LPVOID)detour_NtDevIoCtl, (LPVOID *)&g_orig) != MH_OK ||
        MH_EnableHook(MH_ALL_HOOKS) != MH_OK) {
        logf("[tap] hook setup FAILED\n");
        OutputDebugStringA("vibemote-tap: hook setup failed\n");
        CloseHandle(g_pipe);
        FreeLibraryAndExitThread(g_self, 1);
        return 0;
    }
    logf("[tap] hook enabled, waiting for client...\n");

    if (!ConnectNamedPipe(g_pipe, NULL) && GetLastError() != ERROR_PIPE_CONNECTED) {
        /* 主程序一直不来（注入器只注入不连？）—— 等 60s 再放弃 */
        Sleep(60000);
    }

    for (;;)                                   /* ===== 会话循环 ===== */
    {
    g_pipe_ok = 1;
    /* 协议握手：主程序据此识别 DLL 版本。WUDFHost 常驻 —— 主程序部署新版后
       连到的可能还是旧版驻留 DLL（管道名不变），靠这行发现并升级重注入。 */
    send_line("tap 3\n");
    send_hb();

    char rbuf[512];
    DWORD rd = 0;
    DWORD last_hb = GetTickCount();
    BOOL alive = TRUE;

    while (alive && !g_stopping) {
        /* 收指令（阻塞读；主程序不发东西时靠心跳超时轮询唤醒检查） */
        BOOL pok = PeekNamedPipe(g_pipe, NULL, 0, NULL, &rd, NULL);
        if (pok && rd > 0) {
            if (ReadFile(g_pipe, rbuf, sizeof(rbuf) - 1, &rd, NULL) && rd > 0) {
                rbuf[rd] = '\0';
                char *line = rbuf;
                while (line && *line) {
                    char *nl = strchr(line, '\n');
                    if (nl) *nl = '\0';
                    if (!strncmp(line, "block ", 6))
                        apply_block(line + 6);
                    else if (!strncmp(line, "filter ", 7))
                        apply_filter(line + 7);
                    else if (!strcmp(line, "ping"))
                        send_hb();
                    else if (!strcmp(line, "unload")) {
                        InterlockedExchange(&g_stopping, 1);
                        alive = FALSE;
                    }
                    line = nl ? nl + 1 : NULL;
                }
            } else {
                alive = FALSE;                  /* 管道断：主程序没了 */
            }
        } else if (!pok) {
            alive = FALSE;                      /* Peek 出错 = 管道死亡 */
        } else {
            Sleep(200);
            DWORD now = GetTickCount();
            if (now - last_hb >= HB_PERIOD_MS) {
                send_hb();
                last_hb = now;
            }
        }
    }

    g_pipe_ok = 0;
    if (g_stopping)
        break;                                  /* unload / 宽限到期 → 注销 */
    FlushFileBuffers(g_pipe);
    DisconnectNamedPipe(g_pipe);
    /* 断开 ≠ 死刑：回到监听，30 秒宽限内主程序重连就复用（免一次 UAC 重注入） */
    HANDLE tk = CreateThread(NULL, 0, death_timer, NULL, 0, NULL);
    ConnectNamedPipe(g_pipe, NULL);
    if (g_stopping) {                           /* 被宽限看护自连唤醒 → 注销 */
        if (tk) WaitForSingleObject(tk, 2000);
        break;
    }
    /* 真·新客户端连上：继续会话循环（death_timer 30s 后见 g_pipe_ok 自行退出） */
    }

    /* ===== 注销流程（退出/断开的唯一出口）=====
       1) 先封发送，再摘钩；2) 宽限 500ms 让在飞的调用走完 trampoline；
       3) 反初始化 MinHook、关管道、自卸。 */
    g_pipe_ok = 0;
    if (g_orig) {
        MH_DisableHook(MH_ALL_HOOKS);
        Sleep(500);
        MH_Uninitialize();
    }
    EnterCriticalSection(&g_send_cs);
    CloseHandle(g_pipe);
    g_pipe = INVALID_HANDLE_VALUE;
    LeaveCriticalSection(&g_send_cs);
    DeleteCriticalSection(&g_send_cs);
    OutputDebugStringA("vibemote-tap: unloaded cleanly\n");
    FreeLibraryAndExitThread(g_self, 0);
    return 0;
}

/* ---------------- 入口 ---------------- */
__declspec(dllexport) int tap_api_version(void) { return 2; }   /* 防空导出 */

BOOL APIENTRY DllMain(HMODULE mod, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(mod);
        g_self = mod;
        InitializeCriticalSection(&g_send_cs);
        InitializeCriticalSection(&g_log_cs);
        logf("[tap] DllMain attach, creating thread (pid=%lu)\n", GetCurrentProcessId());
        g_thread = CreateThread(NULL, 0, pipe_thread, NULL, 0, NULL);
        if (!g_thread)
            return FALSE;
    }
    return TRUE;
}
