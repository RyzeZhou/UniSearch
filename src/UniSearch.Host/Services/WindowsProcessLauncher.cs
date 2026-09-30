using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using UniSearch.Sdk.Runtime;

namespace UniSearch.Host.Services;

/// <summary>
/// IProcessLauncher 实现：通用动作统一走 Windows Shell。
/// <para>
/// 打开/定位用 <c>Process.Start(UseShellExecute=true)</c> 就够；但 <b>动词类动作必须自己 P/Invoke
/// <see cref="ShellExecuteEx"/></b>：shell 的 <c>properties</c> 动词要求 <c>SEE_MASK_INVOKEIDLIST</c>
/// 标志，而 <c>ProcessStartInfo.Verb</c> 不设这个位，结果必然失败
/// （报"没有关联程序"）—— 这就是之前"打开属性失败"的根因。
/// </para>
/// </summary>
public sealed class WindowsProcessLauncher : IProcessLauncher
{
    readonly IUniSearchLog _log;

    public WindowsProcessLauncher(IUniSearchLog log) => _log = log;

    // ── ShellExecuteEx ─────────────────────────────────
    const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;   // properties 等 verb 必需
    const uint SEE_MASK_FLAG_NO_UI = 0x00000400;     // 失败时不要弹系统错误框，交给我们报告
    const int SW_SHOWNORMAL = 1;
    const int ERROR_CANCELLED = 1223;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpVerb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);

    /// <summary>走 ShellExecuteEx。返回失败时把 Win32 错误码写进日志，便于区分"没关联程序"和"用户取消"。</summary>
    bool ShellExec(string verb, string file, string? args = null, string? dir = null)
    {
        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask = SEE_MASK_FLAG_NO_UI | SEE_MASK_INVOKEIDLIST,
            lpVerb = verb,
            lpFile = file,
            lpParameters = args,
            lpDirectory = dir,
            nShow = SW_SHOWNORMAL,
        };

        try
        {
            if (ShellExecuteEx(ref info)) return true;

            var err = Marshal.GetLastWin32Error();
            if (err == ERROR_CANCELLED)
                _log.Info("host", $"用户取消了操作（{verb}）：{file}");
            else
                _log.Warn("host", $"ShellExecuteEx 失败 verb={verb} err={err}：{file}");
            return false;
        }
        catch (Exception ex)
        {
            _log.Warn("host", $"ShellExecuteEx 异常 verb={verb}：{file}", ex);
            return false;
        }
    }

    // ── 打开 ───────────────────────────────────────────

    static bool TryStart(ProcessStartInfo psi, IUniSearchLog log, string what)
    {
        try
        {
            psi.UseShellExecute = true;
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            log.Warn("host", $"{what} 失败：{psi.FileName} {psi.Arguments}", ex);
            return false;
        }
    }

    public bool OpenFile(string path, string? arguments = null, string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo(path)
        {
            Arguments = arguments ?? "",
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(path) ?? "",
        };
        return TryStart(psi, _log, "打开文件");
    }

    public bool OpenFolder(string path, bool selectFile = false)
    {
        // explorer 不支持直接"打开并选中"，只能 explorer.exe /select,<路径>
        var psi = new ProcessStartInfo("explorer.exe")
        {
            Arguments = selectFile ? $"/select,\"{path}\"" : $"\"{path}\"",
        };
        return TryStart(psi, _log, "打开目录");
    }

    public bool StartUri(string uri) => TryStart(new ProcessStartInfo(uri), _log, "打开链接");

    // ── 动词类 ─────────────────────────────────────────

    public bool RunVerb(string path, string verb) => ShellExec(verb, path);

    public bool RunAsAdmin(string path, string? arguments = null)
    {
        // UAC 被拒 = 用户取消，不算错误（EverythingToolbar 的处理方式）
        return ShellExec("runas", path, arguments);
    }

    /// <summary>
    /// 在终端中打开目录。优先 Windows Terminal（<c>wt -d</c>），没有就退回 <c>cmd /k cd /d</c>。
    /// 注意不能用 <c>cmd /c start</c> —— 那只会再弹一个资源管理器，不是"在终端打开"。
    /// </summary>
    public bool OpenInConsole(string path)
    {
        var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir)) return false;

        if (TryStart(new ProcessStartInfo("wt.exe") { Arguments = $"-d \"{dir}\"" }, _log, "wt 启动（忽略）"))
            return true;

        return TryStart(new ProcessStartInfo("cmd.exe")
        {
            Arguments = "/k cd /d \"" + dir + "\"",
            WorkingDirectory = dir,
        }, _log, "打开终端");
    }

    // ── 剪贴板 ─────────────────────────────────────────

    /// <summary>
    /// Provider 动作的"复制"落点。必须在 STA 线程上调（WPF 剪贴板的硬要求）——
    /// 动作都从 UI 命令进来，天然满足；真从后台线程调时先封送，别让调用方操心。
    /// </summary>
    public bool CopyToClipboard(string text)
    {
        try
        {
            if (string.IsNullOrEmpty(text)) return false;
            if (System.Threading.Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                return System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    System.Windows.Clipboard.SetText(text);
                    return true;
                }) ?? false;
            System.Windows.Clipboard.SetText(text);
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn("host", "写剪贴板失败", ex);
            return false;
        }
    }
}