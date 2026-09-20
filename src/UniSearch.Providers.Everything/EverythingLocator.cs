using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace UniSearch.Providers.Everything;

/// <summary>
/// 探测 Everything 是否在运行、什么版本、有哪些实例。
/// 只用已核实的公开常量（docs/research/EVERYTHING-IPC-VERIFIED.md 表 A/G），不做任何查询。
/// </summary>
public static class EverythingLocator
{
    /// <summary>IPC 服务端窗口类（1.4 与 1.5 主实例）。</summary>
    public const string IpcWindowClass = "EVERYTHING_TASKBAR_NOTIFICATION";

    const uint WmIpc = 0x0400;              // EVERYTHING_WM_IPC == WM_USER
    const uint ReqMajor = 0, ReqMinor = 1, ReqRevision = 2, ReqBuild = 3, ReqTargetMachine = 5;

    public sealed record Installation(bool Present, Version? Version, string? InstanceName, nint Handle)
    {
        public static Installation NotFound { get; } = new(false, null, null, 0);
        public bool SupportsIpc => Present && Version is not null && Version.Build >= 0 && Version.Major >= 1 && Version.Minor >= 4;
    }

    /// <summary>主实例探测。找不到返回 <see cref="Installation.NotFound"/>。</summary>
    public static Installation Probe()
    {
        var hwnd = FindWindowW(IpcWindowClass, null);
        if (hwnd == 0) return Installation.NotFound;
        return Describe(hwnd, null);
    }

    /// <summary>枚举全部实例（Everything 支持多实例，1.5 默认实例类名带 <c>_(1.5a)</c> 之类后缀）。</summary>
    public static IReadOnlyList<Installation> ProbeAll()
    {
        var found = new List<Installation>();
        EnumWindows((h, _) =>
        {
            var cls = new StringBuilder(256);
            if (GetClassNameW(h, cls, cls.Capacity) <= 0) return true;
            var name = cls.ToString();
            if (!name.StartsWith(IpcWindowClass, StringComparison.Ordinal)) return true;

            string? instance = null;
            if (name.Length > IpcWindowClass.Length + 2 &&
                name[IpcWindowClass.Length] == '(' && name[^1] == ')')
                instance = name[(IpcWindowClass.Length + 1)..^1];

            found.Add(Describe(h, instance));
            return true;
        }, nint.Zero);
        return found;
    }

    static Installation Describe(nint hwnd, string? instance)
    {
        var major = Send(hwnd, ReqMajor);
        var minor = Send(hwnd, ReqMinor);
        var rev = Send(hwnd, ReqRevision);
        var build = Send(hwnd, ReqBuild);
        Version? v = major > 0 ? new Version(major, Math.Max(0, minor), Math.Max(0, rev), Math.Max(0, build)) : null;
        return new Installation(true, v, instance, hwnd);
    }

    static int Send(nint hwnd, uint request)
    {
        try { return SendMessageTimeoutW(hwnd, WmIpc, (nint)(int)request, 0, SMTO_ABORTIFHUNG, 300, out var result).ToInt32() == 0 ? 0 : result.ToInt32(); }
        catch { return 0; }
    }

    // ── 可执行文件定位（"在 Everything 中搜索"按钮要用）──────────────

    /// <summary>
    /// 找到 Everything.exe 的完整路径，找不到返回 null。
    /// <para>
    /// 顺序有讲究：<b>先问正在运行的那个 IPC 宿主进程</b>。Everything 常被便携安装
    /// （本机就是 <c>D:\Everything\everything.exe</c>，注册表里什么都没有），
    /// 而"正在提供 IPC 的那个进程"必然就是我们要唤起的那一个 —— 多实例时也不会挑错。
    /// 注册表和常见路径只作为未运行时的兜底。
    /// </para>
    /// </summary>
    public static string? FindExecutable()
    {
        if (Probe() is { Present: true, Handle: var h } && h != 0 && FromWindow(h) is { } p1)
            return p1;

        foreach (var inst in ProbeAll())
            if (inst.Handle != 0 && FromWindow(inst.Handle) is { } p2)
                return p2;

        foreach (var candidate in FromRegistry()) return candidate;
        foreach (var candidate in FromCommonPaths()) return candidate;
        return null;
    }

    /// <summary>由窗口句柄反查进程映像路径（QueryFullProcessImageName，32/64 位都适用）。</summary>
    static string? FromWindow(nint hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return null;

            const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
            var proc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (proc == 0) return null;
            try
            {
                var sb = new StringBuilder(1024);
                var len = (uint)sb.Capacity;
                if (!QueryFullProcessImageNameW(proc, 0, sb, ref len)) return null;
                var path = sb.ToString(0, (int)len);
                return File.Exists(path) ? path : null;
            }
            finally { CloseHandle(proc); }
        }
        catch { return null; }
    }

    static IEnumerable<string> FromRegistry()
    {
        string[] keys =
        [
            @"SOFTWARE\voidtools\Everything",
            @"SOFTWARE\WOW6432Node\voidtools\Everything",
        ];
        string[] valueNames = ["InstallPath", "InstallLocation", "ExePath", "Path"];

        foreach (var hive in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
            foreach (var sub in keys)
            {
                Microsoft.Win32.RegistryKey? key = null;
                try { key = hive.OpenSubKey(sub); } catch { /* 权限/不存在 */ }
                if (key is null) continue;
                using (key)
                {
                    foreach (var vn in valueNames)
                    {
                        if (key.GetValue(vn) is not string raw || raw.Length == 0) continue;
                        var exe = raw.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                            ? raw
                            : Path.Combine(raw, "Everything.exe");
                        if (File.Exists(exe)) yield return exe;
                    }
                }
            }
    }

    static IEnumerable<string> FromCommonPaths()
    {
        string[] dirs =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            @"D:\Everything",
            @"C:\Everything",
            @"D:\tools\Everything",
        ];
        foreach (var d in dirs)
        {
            if (string.IsNullOrEmpty(d)) continue;
            var exe = Path.Combine(d, "Everything.exe");
            if (File.Exists(exe)) yield return exe;
        }
    }

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern nint OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(nint hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool QueryFullProcessImageNameW(nint hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

    const uint SMTO_ABORTIFHUNG = 0x0002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern nint FindWindowW(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassNameW(nint hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern nint SendMessageTimeoutW(nint hWnd, uint msg, nint wParam, nint lParam, uint flags, uint timeoutMs, out nint result);

    delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);
}
