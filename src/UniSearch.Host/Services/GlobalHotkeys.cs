using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using NHotkey;
using NHotkey.Wpf;
using System.Diagnostics;
using Windows.Win32.System.Variant;
using Windows.Win32.UI.Shell;
using System.IO;
using System.Runtime.InteropServices;
using UniSearch.Sdk.Capabilities;

namespace UniSearch.Host.Services;

/// <summary>
/// 全局热键。
/// <para>
/// <b>两个键，性质不同，实现也不同：</b>
/// </para>
/// <list type="bullet">
/// <item><c>Alt+Win+Space</c> —— 唤出，范围 = 全局，不碰任何程序。
///   用 <see cref="HotkeyManager"/>（<c>RegisterHotKey</c>，EverythingToolbar / FlowLauncher 同款）：
///   这个组合键本来就没人用，独占注册没有副作用。</item>
/// <item><c>Ctrl+F</c> —— 唤出并限定到<b>前台 Explorer 的当前目录</b>。
///   <b>不能用 <c>RegisterHotKey</c></b>：那是系统级独占，一旦注册，记事本/浏览器/编辑器里的
///   "查找"全部失效（用户实测报过："在记事本按 Ctrl+F 也把 UniSearch 呼出来了"）。
///   改用<b>低级键盘钩子</b>（见 <see cref="BlockingKeyHotkey"/>）：回调里先看一眼前台是不是
///   资源管理器，不是就原样放行 —— 只在资源管理器里接管它自己的 Ctrl+F。</item>
/// </list>
/// <para>
/// <b>为什么不用自己写 RegisterHotKey</b>：热键注册失败的口径不好拿捏 ——
/// "已经被别的程序注册了"是常态而不是异常。NHotkey 用命名热键 +
/// <c>HotkeyAlreadyRegistered</c> 事件把这件事显式化。
/// </para>
/// </summary>
public sealed class GlobalHotkeys : IDisposable
{
    readonly Func<SearchContext> _globalContextFactory;
    readonly Action<SearchContext> _showWith;
    readonly Action<string> _trace;
    readonly BlockingKeyHotkey _directoryScopeHotkey;

    /// <summary>目录限定键当前该拦什么（设置里可以改；钩子每次回调都重新读）。</summary>
    KeyGesture? _directoryScopeGesture;

    /// <summary>注册结果（诊断面板与启动日志用）。</summary>
    public IReadOnlyList<(string Name, string Gesture, bool Ok)> Registered { get; private set; } = [];

    /// <param name="showWith">
    /// "带着某个上下文唤出" —— <b>必须包含把窗口显示到前台</b>。
    /// 早期版本这里只 SetContext 不 Show，热键按下去只重跑了一次搜索，窗口还藏在后面，
    /// 用户看到的现象就是"热键没反应"。
    /// </param>
    public GlobalHotkeys(
        Func<SearchContext> globalContextFactory,
        Action<SearchContext> showWith,
        Action<string>? trace = null)
    {
        _globalContextFactory = globalContextFactory;
        _showWith = showWith;
        // 这里千万别写成 `_trace = m => _trace(m)`：那是指向自己的无限递归，
        // 热键一按下就 StackOverflow —— 而且是**不可捕获**的，进程直接消失、日志一个字都不留。
        // 必须先抓住构造参数，别再引用字段本身。
        _trace = m => { if (m is { Length: > 0 }) trace?.Invoke(m); };
        _directoryScopeHotkey = new BlockingKeyHotkey(
            gesture: () => _directoryScopeGesture,
            foregroundMatches: ExplorerLocator.IsForegroundExplorer,
            onTrigger: OnDirectoryScope);
    }

    readonly List<string> _registeredNames = [];

    /// <summary>目录限定键的钩子是否装上了（诊断用）。</summary>
    public bool DirectoryScopeHookInstalled => _directoryScopeHotkey.IsInstalled;

    /// <summary>
    /// 按设置注册热键。<b>可以反复调用</b>：设置里改了键就再调一次，旧的（含钩子）全摘掉、按新设置重来，
    /// 不用重启程序。失败（撞了别的程序）不抛异常，结果记在 <see cref="Registered"/> 里给界面显示。
    /// </summary>
    public void Apply(Settings.HotkeySettings settings)
    {
        RemoveAll();
        var list = new List<(string, string, bool)>();

        if (!settings.Enabled)
        {
            Registered = list;
            _trace("热键已在设置中禁用");
            return;
        }

        // 目录限定键：低级钩子，只在"前台是资源管理器"时拦截（别的程序里的同名组合键不受影响）
        if (string.IsNullOrWhiteSpace(settings.DirectoryScope))
        {
            _directoryScopeGesture = null;
            _trace("目录限定热键未设置，跳过（留空是允许的）");
        }
        else if (Settings.Gestures.Parse(settings.DirectoryScope) is { } dirGesture)
        {
            _directoryScopeGesture = dirGesture;
            var ok = _directoryScopeHotkey.Install();
            list.Add(("unisearch.ctrldir",
                      dirGesture.GetDisplayStringForCulture(null) + "（仅前台是资源管理器时）", ok));
            if (!ok) _trace($"目录限定热键「{settings.DirectoryScope}」钩子安装失败（错误码 {Marshal.GetLastWin32Error()}）");
        }
        else
        {
            _directoryScopeGesture = null;
            _trace($"目录限定热键「{settings.DirectoryScope}」无法解析，已跳过");
        }

        // 全局唤出键
        if (string.IsNullOrWhiteSpace(settings.Summon))
            _trace("唤出热键未设置，跳过（留空是允许的）");
        else if (Settings.Gestures.Parse(settings.Summon) is { } summonGesture)
            list.Add(Register("unisearch.summon", summonGesture, OnSummon, settings.Summon));
        else
            _trace($"唤出热键「{settings.Summon}」无法解析，已跳过");

        Registered = list;
    }

    /// <summary>
    /// 目录限定键语义：限定到前台资源管理器目录；前台不是资源管理器时<b>根本不会走到这里</b>
    /// （钩子已经放行了）。真的读不到目录（"此电脑"、回收站等）才退回全局。
    /// </summary>
    void OnDirectoryScope()
    {
        // 热键按下时焦点多半还在别的程序里；先看它是不是 Explorer
        var dir = ExplorerLocator.GetCurrentDirectory();
        if (dir is { Length: > 0 })
        {
            _trace($"目录限定热键：Explorer 目录 {dir}");
            _showWith(SearchContext.InDirectory(dir, QueryOrigin.ExplorerHotkey));
        }
        else
        {
            _trace("目录限定热键：前台不是 Explorer，退回全局");
            _showWith(_globalContextFactory());
        }
    }

    void OnSummon()
    {
        _trace("全局唤出热键（全局范围）");
        _showWith(_globalContextFactory());
    }

    void RemoveAll()
    {
        _directoryScopeHotkey.Uninstall();
        foreach (var name in _registeredNames)
            try { HotkeyManager.Current.Remove(name); } catch { /* 没注册上/已移除 */ }
        _registeredNames.Clear();
    }

    (string Name, string Gesture, bool Ok) Register(string name, KeyGesture gesture, Action handler, string wanted)
    {
        var key = gesture.Key;
        var modifiers = gesture.Modifiers;
        try
        {
            // 参数类型要写全：AddOrReplace 有 5 个重载，用 (_, _) 会让编译器推断不出委托类型
            HotkeyManager.Current.AddOrReplace(name, key, modifiers,
                (object? _, HotkeyEventArgs _) =>
                {
                    // 热键回调里抛异常等于整个应用崩掉（用户正在别的程序里打字，根本没有心理准备）。
                    // 兜住并记一条，热键失灵也不该带走进程。
                    try { handler(); }
                    catch (Exception ex) { _trace($"{name} 热键处理失败：{ex.GetType().Name}: {ex.Message}"); }
                });
            _registeredNames.Add(name);
            return (name, gesture.GetDisplayStringForCulture(null), true);
        }
        catch (HotkeyAlreadyRegisteredException ex)
        {
            // 被别的启动器占住了：不是失败，是"这台机器上这个键另有其主"。
            // 记下来给用户看，别让它看起来像故障。
            return (name, gesture.GetDisplayStringForCulture(null), false);
        }
    }

    public void Dispose() => RemoveAll();
}

/// <summary>
/// <b>只在指定前台程序里拦截</b>某个组合键的低级键盘钩子（<c>WH_KEYBOARD_LL</c>）。
/// <para>
/// 为什么需要它：<c>RegisterHotKey</c> 是系统级独占 —— 注册 Ctrl+F 之后，记事本、浏览器、
/// 编辑器里的"查找"全部失效（用户实测报过："在记事本按 Ctrl+F 也把 UniSearch 呼出来了"）。
/// 低级钩子可以在回调里先判断"前台是不是资源管理器"，不是就原样 <c>CallNextHookEx</c> 放行。
/// </para>
/// <para>
/// 实现上有四个必须写对的点：
/// <list type="number">
/// <item>回调委托<b>要存成字段</b>：被 GC 回收会让回调变成野指针，系统随即把钩子撤掉（静默失效）；</item>
/// <item>钩子在<b>装它的那个线程</b>（WPF UI 线程）的消息循环里回调，所以直接操作窗口是安全的；</item>
/// <item>回调<b>会被每一次系统级按键触发</b>，必须先用最便宜的判断（键码 / 修饰键）短路，
///   最后才做"前台是谁"的进程查询，否则会撞上系统的 LowLevelHooksTimeout 被摘掉；</item>
/// <item>长按会连续发 KEYDOWN，用一个 latch 去抖，别把唤出触发几十次。</item>
/// </list>
/// </para>
/// </summary>
internal sealed class BlockingKeyHotkey : IDisposable
{
    const int WH_KEYBOARD_LL = 13;
    const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    delegate nint HookProc(int nCode, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT
    {
        public uint vkCode, scanCode, flags, time;
        public nint dwExtraInfo;
    }

    readonly HookProc _proc;
    readonly Func<KeyGesture?> _gesture;
    readonly Func<bool> _foregroundMatches;
    readonly Action _onTrigger;
    nint _handle;
    int _latched;

    public BlockingKeyHotkey(Func<KeyGesture?> gesture, Func<bool> foregroundMatches, Action onTrigger)
    {
        _gesture = gesture;
        _foregroundMatches = foregroundMatches;
        _onTrigger = onTrigger;
        _proc = Callback;
    }

    public bool IsInstalled => _handle != 0;

    public bool Install()
    {
        Uninstall();
        _handle = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        return _handle != 0;
    }

    public void Uninstall()
    {
        if (_handle == 0) return;
        try { UnhookWindowsHookEx(_handle); } catch { /* 退出路径上忽略 */ }
        _handle = 0;
        Interlocked.Exchange(ref _latched, 0);
    }

    public void Dispose() => Uninstall();

    /// <summary>
    /// "这次按键要不要拦下来"。抽成纯函数是为了能被自检直接断言 ——
    /// 钩子回调本身没法测（要靠真实键盘），但这条判定逻辑是整件事的关键。
    /// </summary>
    internal static bool ShouldIntercept(KeyGesture? gesture, ModifierKeys pressed, int vkCode, bool foregroundMatches)
        => gesture is not null
           && foregroundMatches
           && vkCode == KeyInterop.VirtualKeyFromKey(gesture.Key)
           && pressed == gesture.Modifiers;   // 多按/少按修饰键都放行

    nint Callback(int nCode, nuint wParam, nint lParam)
    {
        try
        {
            if (nCode < 0) return CallNextHookEx(_handle, nCode, wParam, lParam);

            var gesture = _gesture();
            if (gesture is null) return CallNextHookEx(_handle, nCode, wParam, lParam);

            var msg = (int)wParam;
            var vk = (int)Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam).vkCode;
            var wanted = KeyInterop.VirtualKeyFromKey(gesture.Key);

            if (msg is WM_KEYUP or WM_SYSKEYUP)
            {
                if (vk == wanted) Interlocked.Exchange(ref _latched, 0);
                return CallNextHookEx(_handle, nCode, wParam, lParam);
            }
            if (msg is not (WM_KEYDOWN or WM_SYSKEYDOWN)) return CallNextHookEx(_handle, nCode, wParam, lParam);
            if (vk != wanted) return CallNextHookEx(_handle, nCode, wParam, lParam);   // 最便宜的短路

            if (!ShouldIntercept(gesture, CurrentModifiers(), vk, _foregroundMatches()))
                return CallNextHookEx(_handle, nCode, wParam, lParam);                 // 放行给前台程序

            if (Interlocked.Exchange(ref _latched, 1) == 1) return 1;                  // 长按重复：吞掉但不重复触发
            _onTrigger();
            return 1;                                                                  // 吞掉：目标程序也不再收到它
        }
        catch
        {
            return CallNextHookEx(_handle, nCode, wParam, lParam);                      // 钩子里绝不能抛
        }
    }

    static ModifierKeys CurrentModifiers()
    {
        var m = ModifierKeys.None;
        if (IsDown(VK_CONTROL)) m |= ModifierKeys.Control;
        if (IsDown(VK_SHIFT)) m |= ModifierKeys.Shift;
        if (IsDown(VK_MENU)) m |= ModifierKeys.Alt;
        if (IsDown(VK_LWIN) || IsDown(VK_RWIN)) m |= ModifierKeys.Windows;
        return m;
    }

    static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    [DllImport("user32.dll", SetLastError = true)]
    static extern nint SetWindowsHookEx(int idHook, HookProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    static extern nint CallNextHookEx(nint hhk, int nCode, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern nint GetModuleHandle(string? lpModuleName);
}

/// <summary>
/// 把窗口真正推到前台。<b>光调 <see cref="Window.Activate"/> 不够</b>：
/// Windows 的前台锁（foreground lock）会让"不是当前前台进程"的激活请求静默失败 ——
/// 窗口在任务栏里闪一下但不出来，用户看到的就是"热键没反应"。
/// </summary>
internal static class WindowSummoner
{
    public static void BringToFront(Window w)
    {
        if (w.Dispatcher.CheckAccess()) Bring(w);
        else w.Dispatcher.BeginInvoke(() => Bring(w));
    }

    static void Bring(Window w)
    {
        if (!w.IsVisible) w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;

        w.Activate();

        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == nint.Zero) return;
        if (GetForegroundWindow() == hwnd) return;

        // 先试正规途径；热键触发的进程通常已被放行，这一步大多数情况就够了
        SetForegroundWindow(hwnd);
        if (GetForegroundWindow() == hwnd) return;

        // 还不行就用 Topmost 抖一下再还原 —— 这是唯一不依赖 AttachThreadInput 的可靠兜底
        var top = w.Topmost;
        w.Topmost = true;
        w.Topmost = top;
        w.Activate();
    }

    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetForegroundWindow(nint hWnd);
}

/// <summary>
/// 取<b>前台</b> Explorer 窗口的当前目录。移植自 FlowLauncher 的 DialogJump（MIT）：
/// 前台窗口属于 <c>explorer.exe</c> 时，枚举 <c>IShellWindows</c> 把 HWND 对到
/// <c>IWebBrowser2</c>，读 <c>LocationURL</c>（形如 <c>file:///D:/Research/AI</c>）。
/// <para>
/// <b>为什么不用 PowerShell 里那条 <c>New-Object -ComObject Shell.Application</c> 的路</b>：
/// 那条路在 PS 引擎里能用（已验证），但 .NET Core <b>不支持 IDispatch 晚绑定</b>，
/// 所以必须走声明式互操作 —— 用 CsWin32 生成的 <c>IShellWindows</c>/<c>IWebBrowser2</c>。
/// </para>
/// </summary>
public static class ExplorerLocator
{
    /// <summary>
    /// 前台窗口是否属于 <c>explorer.exe</c>。低级键盘钩子用它决定"要不要拦这个组合键" ——
    /// 只做进程名查询，不做 COM，够快也够稳。
    /// </summary>
    public static bool IsForegroundExplorer() => IsExplorerWindow(GetForegroundWindow());

    static bool IsExplorerWindow(nint hwnd)
    {
        if (hwnd == 0) return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }   // 进程已退出等，按"不是 Explorer"处理
    }

    public static string? GetCurrentDirectory()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == 0) return null;
        if (!IsExplorerWindow(hwnd)) return null;

        try
        {
            // ShellWindows.CreateInstance<T>：coclass 不能直接 new（CsWin32 不支持）
            var windows = ShellWindows.CreateInstance<IShellWindows>();
            var count = windows.get_Count();           // 实测 Item 索引从 0 开始
            for (var i = 0; i < count; i++)
            {
                // Item 要的是 VARIANT；CsWin32 生成的是裸结构，手动填 VT_I4 + lVal
                var index = default(VARIANT);
                index.vt = VARENUM.VT_I4;
                index.lVal = i;

                if (windows.Item(index) is not IWebBrowser2 wb) continue;
                if (wb.get_HWND() != (nint)hwnd) continue;   // 只认前台这一个窗口

                // 注意：CsWin32 生成的 COM 接口成员是方法形式（get_Xxx），不是 C# 属性
                var url = wb.get_LocationURL().ToString();   // LocationURL 是 BSTR
                if (string.IsNullOrEmpty(url)) continue;
                return FromShellUrl(url);
            }
        }
        catch (COMException) { return null; }          // COM 出错就降级，别让热键崩掉
        catch (InvalidCastException) { return null; }  // coclass/接口不匹配同理
        catch (DllNotFoundException) { return null; }
        return null;
    }

    /// <summary><c>file:///D:/Research/AI</c> → <c>D:\Research\AI</c>（带 URL 解码）。</summary>
    internal static string? FromShellUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeFile) return null;   // 只认本地目录
        var local = uri.LocalPath;
        return Directory.Exists(local) ? local : null;
    }

    [DllImport("user32.dll")] static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);
}
