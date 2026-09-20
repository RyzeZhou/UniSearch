namespace UniSearch.Sdk.Capabilities;

public enum SearchMode
{
    /// <summary>全局：来自全局快捷键 / 托盘 / 命令行。范围 = 整个可见文件系统 + 所有知识源。</summary>
    Global = 0,
    /// <summary>文件系统内：来自 Explorer 或桌面的 Ctrl+F，天然带一个当前目录。</summary>
    FileSystem = 1,
}

public enum ScopeKind
{
    /// <summary>不限定目录。</summary>
    Global = 0,
    /// <summary>仅 <see cref="SearchContext.RootPath"/> 一层。</summary>
    CurrentDirectoryOnly = 1,
    /// <summary><see cref="SearchContext.RootPath"/> 及其全部子目录（Explorer Ctrl+F 的默认语义）。</summary>
    CurrentDirectoryRecursive = 2,
    /// <summary>用户选中的若干项之内。</summary>
    Selection = 3,
    /// <summary>一个显式命名范围（Zotero 某个 collection、Obsidian 某个 vault）。</summary>
    NamedScope = 4,
}

/// <summary>查询是从哪里被发起的。影响 UI 行为（是否置顶、是否自动隐藏）与埋点，也影响 Provider 是否该响应。</summary>
public enum QueryOrigin
{
    Unknown = 0,
    /// <summary>Explorer 窗口内 Ctrl+F，由 ShellBridge 转来，带当前标签页路径。</summary>
    ExplorerHotkey = 1,
    /// <summary>用户点了 Explorer 原生搜索框（阶段三能力）。</summary>
    ExplorerSearchBox = 2,
    /// <summary>桌面上按 Ctrl+F（Scope = 桌面目录）。</summary>
    DesktopHotkey = 3,
    /// <summary>自定义全局快捷键，Scope = Global。</summary>
    GlobalHotkey = 4,
    TrayIcon = 5,
    CommandLine = 6,
    /// <summary>UniSearch 自己的窗口内再次输入。</summary>
    Self = 7,
    /// <summary>从其它应用通过 IPC 主动唤起（“在 UniSearch 里搜 xxx”）。</summary>
    ExternalIpc = 8,
}

/// <summary>
/// 一次搜索的上下文。<b>整个项目的核心概念</b>：
/// “在哪个 Explorer 目录里搜，就以哪个目录为 Scope”，并且据此跳过不具备目录限定能力的 Provider。
/// 不可变记录，可安全跨线程传递。
/// </summary>
public sealed record SearchContext
{
    public required SearchMode Mode { get; init; }
    public required ScopeKind Scope { get; init; }

    /// <summary>限定目录（已规范化，无尾分隔符）。<see cref="Mode"/> 为 Global 时为 null。</summary>
    public string? RootPath { get; init; }

    /// <summary>当 <see cref="Scope"/> = Selection 时的选中项路径。</summary>
    public IReadOnlyList<string> SelectedPaths { get; init; } = [];

    public QueryOrigin Origin { get; init; } = QueryOrigin.Unknown;

    /// <summary>发起请求的窗口句柄（用于把结果窗口摆到同一显示器、以及归还焦点）。0 = 未知。</summary>
    public nint OwnerWindow { get; init; }

    /// <summary>NamedScope 的标识，例如 <c>"zotero:collection/ABC123"</c>。</summary>
    public string? NamedScope { get; init; }

    /// <summary>宿主进程名（explorer.exe 等），便于按来源定制。</summary>
    public string? OriginProcess { get; init; }

    public static SearchContext Global(QueryOrigin origin = QueryOrigin.GlobalHotkey, nint owner = 0) => new()
    {
        Mode = SearchMode.Global,
        Scope = ScopeKind.Global,
        Origin = origin,
        OwnerWindow = owner,
    };

    public static SearchContext InDirectory(string path, QueryOrigin origin, bool recursive = true, nint owner = 0) => new()
    {
        Mode = SearchMode.FileSystem,
        Scope = recursive ? ScopeKind.CurrentDirectoryRecursive : ScopeKind.CurrentDirectoryOnly,
        RootPath = Model.Canonicalization.ToDirectoryKey(path),
        Origin = origin,
        OwnerWindow = owner,
    };

    /// <summary>是否为“限定目录”的搜索。调度器据此跳过不具备目录能力的 Provider。</summary>
    public bool IsDirectoryBounded =>
        Mode == SearchMode.FileSystem && !string.IsNullOrWhiteSpace(RootPath);

    /// <summary>给 UI 状态条用的一句话：<c>"D:\Research\AI 及其子目录"</c>。</summary>
    public string Describe() => Scope switch
    {
        ScopeKind.Global => "全局",
        ScopeKind.CurrentDirectoryOnly => RootPath + "（仅本层）",
        ScopeKind.CurrentDirectoryRecursive => RootPath + "（含子目录）",
        ScopeKind.Selection => $"{SelectedPaths.Count} 个选中项内",
        ScopeKind.NamedScope => NamedScope ?? RootPath ?? "命名范围",
        _ => "全局",
    };
}
