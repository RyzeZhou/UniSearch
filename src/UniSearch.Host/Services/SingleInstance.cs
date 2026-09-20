using System.Runtime.InteropServices;

namespace UniSearch.Host.Services;

/// <summary>
/// 单实例守门人 + "再启动一次 = 唤出已有窗口"的跨进程通知。
/// <para>
/// <b>为什么必须有</b>：全局热键是<b>进程级</b>资源。第二个实例注册同一个组合键必然失败，
/// 日志里写的是"被其它程序占用"；托盘里还会多出一个图标，两个窗口各自维护一份搜索状态。
/// 用户看到的现象是"热键时灵时不灵"——这种失败很难归因到"我不小心开了两次"。
/// </para>
/// <para>
/// 做法：命名互斥体判重 + 广播一条自定义窗口消息（<c>RegisterWindowMessage</c> 保证两个进程
/// 拿到同一个消息号）。第二个进程只做两件事：广播、退出。
/// </para>
/// </summary>
public static class SingleInstance
{
    /// <summary>
    /// <c>Local\</c> 前缀 = 每个登录会话一把锁：同一台机器上不同用户各自跑一份是合理的
    /// （各自的托盘、各自的热键），不该互相挡。
    /// </summary>
    const string MutexName = @"Local\UniSearch.SingleInstance.v1";

    /// <summary>唤出消息的注册名。改这个串等于换一套协议，两个实例必须一致。</summary>
    const string MessageName = "UniSearch.Summon.v1";

    const nint HwndBroadcast = 0xffff;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    static Mutex? _mutex;

    /// <summary>唤出消息号（<c>RegisterWindowMessage</c> 的返回值，两个进程相同）。0 = 注册失败。</summary>
    public static uint SummonMessage { get; private set; }

    /// <summary>本进程是否抢到了唯一实例锁。</summary>
    public static bool IsFirstInstance { get; private set; }

    /// <summary>
    /// 抢锁并注册唤出消息。返回 true = 本进程是唯一实例；false = 已经有实例在跑，
    /// 调用方应当 <see cref="BroadcastSummon"/> 之后立刻退出。
    /// </summary>
    public static bool Initialize()
    {
        SummonMessage = RegisterWindowMessage(MessageName);
        if (SummonMessage == 0) SummonMessage = 0xC0DE;   // 极端情况下退化成固定值，仍能自洽

        try
        {
            // initiallyOwned 只在"由本进程创建"时生效：已存在时我们不会持有它
            _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            IsFirstInstance = createdNew;
            if (!createdNew)
            {
                _mutex.Dispose();
                _mutex = null;
            }
        }
        catch (Exception)
        {
            // 拿不到互斥体（权限异常等）时**宁可放行**：让程序能用，
            // 也好过因为一个保护措施而彻底启动不了。
            IsFirstInstance = true;
        }
        return IsFirstInstance;
    }

    /// <summary>通知已有实例把窗口唤到前台。第二个进程调完就该退出。</summary>
    public static void BroadcastSummon()
    {
        if (SummonMessage == 0) return;
        PostMessage(HwndBroadcast, SummonMessage, 0, 0);
    }

    /// <summary>进程退出时释放锁（不释放也不会残留：OS 会回收）。</summary>
    public static void Release()
    {
        try { _mutex?.ReleaseMutex(); } catch { /* 没持有时释放会抛，忽略 */ }
        _mutex?.Dispose();
        _mutex = null;
    }
}
