using System.IO;
using System.NativeTray;
using System.Windows;
using UniSearch.Sdk.Runtime;

namespace UniSearch.Host.Services;

/// <summary>
/// 托盘常驻（NativeTray，MIT；EverythingToolbar 同款）。
/// <para>
/// <b>为什么必须有它</b>：窗口按 X 只是收进后台（热键还要能唤回），进程会一直活着。
/// 没有托盘就没有"看得见、点得到"的退出入口，用户只能去任务管理器杀进程 ——
/// 这正是"关掉窗口后再也呼不出来"那个 bug 修完之后的遗留问题。
/// </para>
/// <para>
/// 托盘菜单里的动作可能从托盘自己的消息窗口回调进来，所以统一经
/// <see cref="Application.Dispatcher"/> 回到 UI 线程再执行。
/// </para>
/// </summary>
public sealed class TrayPresence : IDisposable
{
    TrayIconHost? _host;
    bool _balloonShown;

    TrayPresence() { }

    /// <summary>托盘是否真的建起来了。建失败不该让整个程序起不来，所以用标志位而不是抛异常。</summary>
    public bool Ok { get; private set; }

    /// <summary>失败原因（写进日志供排查）。</summary>
    public string? Error { get; private set; }

    /// <param name="summon">唤出窗口。</param>
    /// <param name="hide">把窗口收进后台。</param>
    /// <param name="settings">打开设置。</param>
    /// <param name="quit">
    /// 真正退出。<b>注意传 <see cref="App.Quit"/> 而不是 <c>Application.Shutdown</c></b> ——
    /// 窗口的 <c>Closing</c> 会取消关闭请求（关闭即隐藏），不先置位就退不掉。
    /// </param>
    public static TrayPresence Create(
        Action summon, Action hide, Action settings, Action quit, IUniSearchLog? log = null)
    {
        var tray = new TrayPresence();
        try
        {
            var menu = new TrayMenu
            {
                new TrayMenuItem { Header = "显示 UniSearch", Command = new TrayCommand(_ => OnUi(summon)) },
                new TrayMenuItem { Header = "隐藏窗口", Command = new TrayCommand(_ => OnUi(hide)) },
                new TraySeparator(),
                new TrayMenuItem { Header = "设置…", Command = new TrayCommand(_ => OnUi(settings)) },
                new TraySeparator(),
                new TrayMenuItem { Header = "退出 UniSearch", Command = new TrayCommand(_ => OnUi(quit)) },
            };

            var host = new TrayIconHost
            {
                ToolTipText = "UniSearch —— 右键显示菜单",
                Menu = menu,
                IsVisible = true,
            };

            if (LoadIcon() is { } icon) host.IconSource = icon;

            // 左键单击直接唤出（启动器的习惯用法，不用先弹菜单）
            host.LeftClick += (_, _) => OnUi(summon);
            host.LeftDoubleClick += (_, _) => OnUi(summon);

            tray._host = host;
            tray.Ok = true;
        }
        catch (Exception ex)
        {
            tray.Error = $"{ex.GetType().Name}: {ex.Message}";
            log?.Warn("tray", "托盘图标创建失败", ex);
        }
        return tray;
    }

    /// <summary>托盘图标句柄是否拿到了（拿不到时 Windows 会显示一个默认图标）。</summary>
    public bool HasIcon => _host?.IconSource is not null || (_host?.Icon ?? nint.Zero) != nint.Zero;

    /// <summary>当前托盘菜单的标题（诊断转储用）。分隔线不占位。</summary>
    public IReadOnlyList<string> MenuHeaders
    {
        get
        {
            if (_host?.Menu is not { } menu) return [];
            var list = new List<string>();
            foreach (var item in menu)
                if (item is TrayMenuItem mi && mi.Header is { Length: > 0 } h) list.Add(h);
            return list;
        }
    }

    /// <summary>
    /// 按标题执行托盘菜单项 —— <b>模拟用户右键点击</b>。
    /// 存在的意义是可验证：托盘菜单点不了（合成鼠标会被 UIPI 拦），
    /// 但"右键 → 退出"这条链路必须能被脚本证伪，不能只靠肉眼点一次。
    /// </summary>
    public bool InvokeMenuItem(string header)
        => InvokeMenuItem(header, out _);

    public bool InvokeMenuItem(string header, out string error)
    {
        error = string.Empty;
        if (_host?.Menu is not { } menu) { error = "托盘未创建"; return false; }

        foreach (var item in menu)
        {
            if (item is not TrayMenuItem mi) continue;
            if (!string.Equals(mi.Header, header, StringComparison.Ordinal)) continue;
            if (mi.Command is null) { error = $"菜单项「{header}」没有绑定命令"; return false; }
            mi.Command.Execute(mi.CommandParameter);
            return true;
        }
        error = $"没有找到菜单项「{header}」";
        return false;
    }

    /// <summary>气泡提示。第一次"关闭即隐藏"时用一次，否则用户会以为程序已经退了。</summary>
    public void NotifyHiddenToTray()
    {
        if (_host is null || _balloonShown) return;
        _balloonShown = true;
        try
        {
            _host.BalloonTipTitle = "UniSearch 仍在后台运行";
            _host.BalloonTipText = "窗口已收进托盘，热键随时可以唤回。右键托盘图标可以退出。";
            _host.ShowBalloonTip(4000);
        }
        catch { /* 气泡失败无所谓，别影响隐藏 */ }
    }

    /// <summary>把动作切回 UI 线程（托盘回调不保证在哪个线程上）。</summary>
    static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) { action(); return; }
        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    /// <summary>从嵌入资源读托盘图标（csproj 里的 &lt;Resource Include="Assets\UniSearch.ico"&gt;）。</summary>
    static Win32Icon? LoadIcon()
    {
        try
        {
            var info = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/UniSearch.ico"));
            if (info?.Stream is null) return null;
            using var buffer = new MemoryStream();
            info.Stream.CopyTo(buffer);
            info.Stream.Dispose();
            // 用 byte[] 重载而不是 Stream：.ico 必须整份在手，Win32Icon 要按帧表挑尺寸
            return new Win32Icon(buffer.ToArray());
        }
        catch
        {
            return null;   // 拿不到图标也要让托盘起来（Windows 会给个默认图标）
        }
    }

    public void Dispose()
    {
        try { _host?.Dispose(); } catch { /* 进程正在退出，失败无所谓 */ }
        _host = null;
        Ok = false;
    }
}
