using System.Runtime.InteropServices;
using System.Windows.Interop;
using Vanara.PInvoke;

namespace UniSearch.Host.Services;

/// <summary>
/// 真正的 Windows shell 右键菜单（`IContextMenu`）：带系统动词 —— "打开方式"、"发送到"、
/// 7-Zip / Git 等第三方注册的动词、以及"属性"。
/// <para>
/// 为什么不用 <see cref="System.Windows.Controls.ContextMenu"/>：那是 WPF 自己的菜单，
/// 只有我们自己塞进去的项，拿不到系统shell 的动词表。要做"真 shell 菜单"就必须走
/// <c>IShellFolder.GetUIObjectOf(IID_IContextMenu)</c>。
/// </para>
/// <para>
/// <b>关键点：owner-draw 消息必须转发给 <c>IContextMenu2/3</c></b>。
/// shell 的菜单项里有自绘项（"发送到"子菜单、图标），Windows 会把
/// <c>WM_DRAWITEM</c>/<c>WM_MEASUREITEM</c>/<c>WM_INITMENUPOPUP</c> 发给<b>菜单的属主窗口</b>。
/// 所以这里建了一个隐藏窗口当属主，在自己的消息钩子里转发 —— 不转发的话子菜单会是空的。
/// </para>
/// <para>
/// 调用方式用 <c>CMINVOKECOMMANDINFOEX.lpVerbW</c>（普通宽字符串）而不是
/// <c>MAKEINTRESOURCE(offset)</c>：前者不需要碰 Vanara 的 <c>ResourceId</c> union，
/// 少一层易错的 marshalling。verb 名经 <c>GetCommandString(GCS_VERBW)</c> 取得。
/// </para>
/// </summary>
public sealed class ShellContextMenu : IDisposable
{
    const uint WM_INITMENUPOPUP = 0x0117;
    const uint WM_DRAWITEM = 0x002B;
    const uint WM_MEASUREITEM = 0x002C;
    const uint WM_MENUCHAR = 0x0120;
    const uint IDCMD_FIRST = 1;
    const uint IDCMD_LAST = 0x7FFF;

    readonly HwndSource _sink;
    Shell32.IContextMenu2? _cm2;
    Shell32.IContextMenu3? _cm3;

    public ShellContextMenu()
    {
        // 消息专用隐藏窗口：只用来当菜单属主接收自绘消息，不显示
        var p = new HwndSourceParameters("UniSearch.ShellMenuSink")
        {
            Width = 0,
            Height = 0,
            PositionX = -32000,
            PositionY = -32000,
            WindowStyle = 0,
        };
        _sink = new HwndSource(p);
        _sink.AddHook(WndProc);
    }

    nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        // IContextMenu3 优先：它还能处理 WM_MENUCHAR；
        // 注意 HandleMenuMsg2 的返回值要经 out 参数交给系统，不能只用 handled
        if (_cm3 is not null)
        {
            if (msg is (int)WM_DRAWITEM or (int)WM_MEASUREITEM or (int)WM_INITMENUPOPUP or (int)WM_MENUCHAR)
            {
                var hr = _cm3.HandleMenuMsg2((uint)msg, wParam, lParam, out var result);
                if (hr.Succeeded)
                {
                    handled = true;
                    return result;
                }
            }
        }
        else if (_cm2 is not null)
        {
            if (msg is (int)WM_DRAWITEM or (int)WM_MEASUREITEM or (int)WM_INITMENUPOPUP)
            {
                _cm2.HandleMenuMsg((uint)msg, wParam, lParam);
                handled = true;
                return 0;
            }
        }
        return 0;
    }

    /// <summary>诊断钩子：宿主接到日志上。菜单链路为了"失败降级不崩"会吞掉细节，没有这条通道就查不出原因。</summary>
    public static Action<string>? Trace { get; set; }

    /// <summary>
    /// 在屏幕坐标 (x, y) 弹出给定路径的 shell 菜单。返回 true 表示用户选了某项并已执行。
    /// <paramref name="extendedVerbs"/> 为 true 时给出扩展动词（对应按住 Shift 右键，例如"以其他用户身份运行"）。
    /// <para>
    /// <paramref name="customItems"/> 会被追加到菜单尾部（id 自 <see cref="CustomIdBase"/> 起），
    /// 由 <paramref name="onCustomItem"/> 按 id 分发 —— 让系统动词与宿主动作出现在同一个菜单里。
    /// </para>
    /// </summary>
    public bool Show(string path, int x, int y, bool extendedVerbs,
                     IReadOnlyList<(uint Id, string Label)>? customItems,
                     Func<uint, bool>? onCustomItem, out string error)
    {
        error = string.Empty;

        var pidl = Shell32.ILCreateFromPath(path);
        if (pidl is null || pidl.IsInvalid) { error = $"拿不到 PIDL：{path}"; return false; }

        nint childPidl = 0;
        try
        {
            var iidFolder = typeof(Shell32.IShellFolder).GUID;
            var hrBound = Shell32.SHBindToParent(pidl, iidFolder, out var psfObj, out childPidl);
            if (hrBound.Failed || psfObj is not Shell32.IShellFolder folder)
            {
                error = $"SHBindToParent 失败：{hrBound}";
                return false;
            }

            var iidMenu = typeof(Shell32.IContextMenu).GUID;
            var hrObj = folder.GetUIObjectOf(_sink.Handle, 1, [childPidl], ref iidMenu, nint.Zero, out var cmObj);
            if (hrObj.Failed || cmObj is not Shell32.IContextMenu cm)
            {
                error = $"取 IContextMenu 失败：{hrObj}";
                return false;
            }

            _cm3 = cmObj as Shell32.IContextMenu3;
            _cm2 = cmObj as Shell32.IContextMenu2;

            using var hMenu = User32.CreatePopupMenu();
            var flags = Shell32.CMF.CMF_NORMAL | (extendedVerbs ? Shell32.CMF.CMF_EXTENDEDVERBS : 0);
            var hrQuery = cm.QueryContextMenu(hMenu, 0, IDCMD_FIRST, IDCMD_LAST, flags);
            if (hrQuery.Failed)
            {
                error = $"QueryContextMenu 失败：{hrQuery}";
                return false;
            }

            // 宿主动作追加到 shell 菜单尾部（id 从 CustomIdBase 起，高于 shell 动词区，
            // 不会撞号）。这样一个菜单里既有系统动词（"打开方式/发送到"/7-Zip），
            // 也有我们带快捷键提示的项 —— 用户不用在两套菜单之间来回。
            if (customItems is { Count: > 0 })
            {
                if (!User32.AppendMenu(hMenu, User32.MenuFlags.MF_SEPARATOR, nint.Zero, null))
                    { error = "追加分隔线失败"; return false; }
                foreach (var (id, label) in customItems)
                    if (!User32.AppendMenu(hMenu, User32.MenuFlags.MF_STRING, (nint)id, label))
                        { error = $"追加项失败：{label}"; return false; }
            }

            // TPM_RETURNCMD：直接返回命令 id 而不用等 WM_COMMAND，省掉一个消息分支
            var total = User32.GetMenuItemCount(hMenu);
            Trace?.Invoke($"菜单就绪：共 {total} 项（宿主附加 {customItems?.Count ?? 0} 个）");

            var cmd = User32.TrackPopupMenuEx(hMenu,
                User32.TrackPopupMenuFlags.TPM_RETURNCMD | User32.TrackPopupMenuFlags.TPM_RIGHTBUTTON,
                x, y, _sink.Handle, null);

            Trace?.Invoke($"菜单返回 cmd=0x{cmd:X}（{(cmd >= CustomIdBase ? "宿主附加项" : cmd == 0 ? "取消" : "shell 动词")}）");

            if (cmd == 0) { error = "用户取消"; return false; }   // 取消不是错误，调用方按 false 处理即可

            // 宿主附加项：id >= CustomIdBase，交给回调执行
            if (cmd >= CustomIdBase)
            {
                if (onCustomItem is null) { error = $"id {cmd} 没有宿主回调可分发"; return false; }
                return onCustomItem(cmd);
            }

            return Invoke(cm, cmd - IDCMD_FIRST, ref error);
        }
        finally
        {
            _cm2 = null;
            _cm3 = null;
            // 注意：childPidl 是**指向父 PIDL 内部的指针**，不是独立分配的内存，
            // 绝不能 FreeCoTaskMem 它 —— 那样会破坏堆并让进程在原生层 fail-fast
            // （托管 catch 抓不到，表现为"无声退出"，极难定位）。
            // 只释放父 PIDL 即可，childPidl 随之失效。
            pidl.Dispose();
        }
    }

    /// <summary>宿主附加项的 id 起点。必须高于 shell 动词区（IDCMD_LAST = 0x7FFF），否则会撞号。</summary>
    public const uint CustomIdBase = 0x8000;

    /// <summary>
    /// 打开 shell 菜单管道到 <c>QueryContextMenu</c> 为止，把系统提供的动词名列出来。
    /// <para>
    /// 存在的意义：<c>TrackPopupMenuEx</c> 会阻塞等用户点击，无法自动化验证；
    /// 而真正容易出错的是它前面的 COM 管道（PIDL → IShellFolder → IContextMenu → 动词表）。
    /// 把这段单独暴露出来，就能用脚本断言"系统动词确实拿到了"，而不是只能靠人眼看菜单。
    /// </para>
    /// </summary>
    public IReadOnlyList<string> ListVerbs(string path, out string error)
    {
        error = string.Empty;
        var verbs = new List<string>();

        var pidl = Shell32.ILCreateFromPath(path);
        if (pidl is null || pidl.IsInvalid) { error = $"拿不到 PIDL：{path}"; return verbs; }

        nint childPidl = 0;
        try
        {
            var iidFolder = typeof(Shell32.IShellFolder).GUID;
            var hrBound = Shell32.SHBindToParent(pidl, iidFolder, out var psfObj, out childPidl);
            if (hrBound.Failed || psfObj is not Shell32.IShellFolder folder)
            {
                error = $"SHBindToParent 失败：{hrBound}";
                return verbs;
            }

            var iidMenu = typeof(Shell32.IContextMenu).GUID;
            var hrObj = folder.GetUIObjectOf(_sink.Handle, 1, [childPidl], ref iidMenu, nint.Zero, out var cmObj);
            if (hrObj.Failed || cmObj is not Shell32.IContextMenu cm)
            {
                error = $"取 IContextMenu 失败：{hrObj}";
                return verbs;
            }

            _cm3 = cmObj as Shell32.IContextMenu3;
            _cm2 = cmObj as Shell32.IContextMenu2;

            using var hMenu = User32.CreatePopupMenu();
            var hrQuery = cm.QueryContextMenu(hMenu, 0, IDCMD_FIRST, IDCMD_LAST, Shell32.CMF.CMF_NORMAL);
            if (hrQuery.Failed) { error = $"QueryContextMenu 失败：{hrQuery}"; return verbs; }

            var count = User32.GetMenuItemCount(hMenu);
            for (uint i = 0; i < (uint)Math.Max(0, count); i++)
            {
                var v = GetVerb(cm, i);
                if (!string.IsNullOrEmpty(v)) verbs.Add(v);
            }
            return verbs;
        }
        finally
        {
            _cm2 = null;
            _cm3 = null;
            // 注意：childPidl 是**指向父 PIDL 内部的指针**，不是独立分配的内存，
            // 绝不能 FreeCoTaskMem 它 —— 那样会破坏堆并让进程在原生层 fail-fast
            // （托管 catch 抓不到，表现为"无声退出"，极难定位）。
            // 只释放父 PIDL 即可，childPidl 随之失效。
            pidl.Dispose();
        }
    }

    /// <summary>
    /// 自行声明的 <c>CMINVOKECOMMANDINFO</c>（14 字段，与原生布局一致）。
    /// <para>
    /// <b>为什么不用 Vanara 的 <c>CMINVOKECOMMANDINFO</c> / <c>EX</c>：</b>
    /// EX 版缺少 <c>hInstApp/lpIDList/lpClass/hkeyClass/hProcess</c>，marshal 出来的内存布局
    /// 比原生短，shell 会读到错位的值；而非 EX 版的 <c>lpVerb</c> 又被声明成 <c>String</c>，
    /// 放不下 <c>MAKEINTRESOURCE(offset)</c> 需要的整数偏移。
    /// </para>
    /// <para>
    /// <b>偏移调用是唯一可靠的方式</b>：实测部分菜单项（如「打开」）不实现
    /// <c>GetCommandString(GCS_VERBW)</c>，按动词名调用会失败（会表现为"点了没反应"）。
    /// </para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct CMInvokeCommandInfo
    {
        public uint cbSize;
        public uint fMask;
        public nint hwnd;
        public nint lpVerb;          // MAKEINTRESOURCE(offset)
        public nint lpParameters;
        public nint lpDirectory;
        public int nShow;
        public nint hInstApp;
        public nint lpIDList;
        public nint lpClass;
        public nint hkeyClass;
        public uint dwHotKey;
        public nint hIcon;
        public nint hProcess;
    }

    const uint CMIC_MASK_FLAG_NO_UI = 0x00000400;

    /// <summary>
    /// 按<b>动词名</b>直接调用（绕过菜单弹窗），供自动化验证 InvokeCommand 的
    /// marshalling 与偏移换算是否正确 —— <c>TrackPopupMenuEx</c> 会阻塞等点击，没法脚本测。
    /// </summary>
    public bool InvokeByVerb(string path, string verb, out string error)
    {
        error = string.Empty;

        var pidl = Shell32.ILCreateFromPath(path);
        if (pidl is null || pidl.IsInvalid) { error = $"拿不到 PIDL：{path}"; return false; }

        nint childPidl = 0;
        try
        {
            var iidFolder = typeof(Shell32.IShellFolder).GUID;
            var hrBound = Shell32.SHBindToParent(pidl, iidFolder, out var psfObj, out childPidl);
            if (hrBound.Failed || psfObj is not Shell32.IShellFolder folder)
            {
                error = $"SHBindToParent 失败：{hrBound}";
                return false;
            }

            var iidMenu = typeof(Shell32.IContextMenu).GUID;
            var hrObj = folder.GetUIObjectOf(_sink.Handle, 1, [childPidl], ref iidMenu, nint.Zero, out var cmObj);
            if (hrObj.Failed || cmObj is not Shell32.IContextMenu cm)
            {
                error = $"取 IContextMenu 失败：{hrObj}";
                return false;
            }

            // 在菜单里找到与目标动词同名的项，拿到它的偏移
            using var hMenu = User32.CreatePopupMenu();
            var hrQuery = cm.QueryContextMenu(hMenu, 0, IDCMD_FIRST, IDCMD_LAST, Shell32.CMF.CMF_NORMAL);
            if (hrQuery.Failed) { error = $"QueryContextMenu 失败：{hrQuery}"; return false; }

            var count = User32.GetMenuItemCount(hMenu);
            for (uint i = 0; i < (uint)Math.Max(0, count); i++)
            {
                if (!string.Equals(GetVerb(cm, i), verb, StringComparison.OrdinalIgnoreCase)) continue;
                var ok = Invoke(cm, i, ref error);
                if (!ok && error.Length == 0) error = $"动词 {verb} 调用失败";
                return ok;
            }

            error = $"菜单里没有动词 {verb}";
            return false;
        }
        finally
        {
            // childPidl 指向父 PIDL 内部，不可单独释放（见 ListVerbs 的注释）
            pidl.Dispose();
        }
    }

    bool Invoke(Shell32.IContextMenu cm, uint offset, ref string error)
    {
        var info = new CMInvokeCommandInfo
        {
            cbSize = (uint)Marshal.SizeOf<CMInvokeCommandInfo>(),
            fMask = CMIC_MASK_FLAG_NO_UI,          // 失败别弹系统错误框，由我们报告
            hwnd = _sink.Handle,
            lpVerb = (nint)offset,                  // MAKEINTRESOURCE：偏移即动词 id
            nShow = 1,                              // SW_SHOWNORMAL
        };

        var size = Marshal.SizeOf<CMInvokeCommandInfo>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            var hr = cm.InvokeCommand(ptr);
            if (hr.Failed) { error = $"InvokeCommand 失败：{hr}"; return false; }
            return true;
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    /// <summary>问 shell 要这个菜单项的标准动词名（诊断用；调用本身走偏移，见 <see cref="Invoke"/>）。</summary>
    static string? GetVerb(Shell32.IContextMenu cm, uint offset)
    {
        const int MaxChars = 256;
        var buf = Marshal.AllocHGlobal(MaxChars * sizeof(char));
        try
        {
            var hr = cm.GetCommandString(offset, Shell32.GCS.GCS_VERBW, nint.Zero, buf, MaxChars);
            if (hr.Failed) return null;
            var verb = Marshal.PtrToStringUni(buf);
            return string.IsNullOrWhiteSpace(verb) ? null : verb;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public void Dispose()
    {
        _sink.RemoveHook(WndProc);
        _sink.Dispose();
    }
}
