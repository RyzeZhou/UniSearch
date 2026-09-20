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
/// 全局热键（用 <see cref="HotkeyManager"/>，EverythingToolbar 与 FlowLauncher 同款）。
/// <para>
/// <b>为什么不用自己写 RegisterHotKey</b>：热键注册失败的口径不好拿捏 ——
/// "已经被别的程序注册了"是常态而不是异常（别的启动器也常占 Ctrl+F）。
/// NHotkey 用命名热键 + <c>HotkeyAlreadyRegistered</i> 事件把这件事显式化。
/// </para>
/// <para>
/// 两个热键，按 UI-SPEC §4.1：
/// <list type="bullet">
/// <item><c>Ctrl+F</c> —— 唤出并限定到<b>前台 Explorer 的当前目录</b>。注意它会全局接管
///   Explorer 里原本的 Ctrl+F（这正是"接管搜索"的语义，属侵入性行为，所以做成可关）。</item>
/// <item><c>Win+Alt+Space</c> —— 唤出，范围=全局，不碰任何程序。</item>
/// </list>
/// </para>
/// </summary>
public sealed class GlobalHotkeys : IDisposable
{
    readonly Func<SearchContext> _globalContextFactory;
    readonly Action<SearchContext> _showWith;
    readonly Action<string> _trace;

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
    }

    readonly List<string> _registeredNames = [];

    /// <summary>
    /// 按设置注册热键。<b>可以反复调用</b>：设置里改了键就再调一次，旧的全摘掉、按新键重来，
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

        // 目录限定键：会全局接管资源管理器原本的 Ctrl+F，冲突时退回全局唤出
        if (string.IsNullOrWhiteSpace(settings.DirectoryScope))
            _trace("目录限定热键未设置，跳过（留空是允许的）");
        else if (Settings.Gestures.Parse(settings.DirectoryScope) is { } dirGesture)
            list.Add(Register("unisearch.ctrldir", dirGesture, OnDirectoryScope, settings.DirectoryScope));
        else
            _trace($"目录限定热键「{settings.DirectoryScope}」无法解析，已跳过");

        // 全局唤出键
        if (string.IsNullOrWhiteSpace(settings.Summon))
            _trace("唤出热键未设置，跳过（留空是允许的）");
        else if (Settings.Gestures.Parse(settings.Summon) is { } summonGesture)
            list.Add(Register("unisearch.summon", summonGesture, OnSummon, settings.Summon));
        else
            _trace($"唤出热键「{settings.Summon}」无法解析，已跳过");

        Registered = list;
    }

    /// <summary>Ctrl+F 语义：限定到前台资源管理器目录；前台不是资源管理器就退回全局。</summary>
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
    public static string? GetCurrentDirectory()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == 0) return null;

        GetWindowThreadProcessId(hwnd, out var pid);
        try
        {
            using var p = Process.GetProcessById((int)pid);
            if (!p.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase)) return null;
        }
        catch { return null; }   // 进程已退出等，按"不是 Explorer"处理

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
