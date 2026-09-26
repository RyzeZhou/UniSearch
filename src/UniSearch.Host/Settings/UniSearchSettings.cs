using System.Text.Json;
using System.Text.Json.Serialization;

namespace UniSearch.Host.Settings;

/// <summary>
/// 用户设置。落盘在 <c>%LOCALAPPDATA%\UniSearch\settings.json</c>。
/// <para>
/// <b>为什么是 JSON 而不是 Config.Net</b>（原计划用的那个，Apache-2.0）：
/// 实测它把 <c>int</c> 写成字符串、把 <c>string[]</c> 存成 <b>空格拼接的单个字符串</b>
/// （<c>{"Excludes": "D:\\a C:\\b"}</c>）—— 排除目录里只要带空格就会散架，而 Windows 路径
/// 带空格是常态。所以改用内置的 <c>System.Text.Json</c>：零新增依赖、原生支持数组、
/// 落盘文件人手改也不会出事。详见 docs/research/REUSE-CANDIDATES.md。
/// </para>
/// <para>
/// 文件是<b>给人看、给人改的</b>：camelCase、缩进、缺字段按默认值、坏文件不静默丢弃（备份后重建）。
/// </para>
/// </summary>
public sealed class UniSearchSettings
{
    /// <summary>配版本号是为了将来能迁移 —— 改字段语义时靠它决定怎么升。
    /// <c>1 → 2</c>：分组视图改平铺单表，<c>search.perCategoryLimit*</c> 由 <c>search.maxRows</c> 取代；
    /// 新增 <c>columns</c> 节。</summary>
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;
    public HotkeySettings Hotkeys { get; set; } = new();
    public SearchSettings Search { get; set; } = new();
    public PreviewSettings Preview { get; set; } = new();
    public WindowSettings Window { get; set; } = new();

    /// <summary>结果表的列布局（列宽/可见列/顺序/排序）。</summary>
    public ColumnsSettings Columns { get; set; } = new();

    /// <summary>多选批量动作（第 12 轮）：压缩包放哪、怎么命名、完成后是否定位。</summary>
    public ArchiveSettings Archive { get; set; } = new();

    /// <summary>按 Provider id 分节；Provider 通过 <c>IProviderRuntime.Settings</c> 读自己那一节。</summary>
    public Dictionary<string, ProviderSettings> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 深拷贝（走一次 JSON 往返）。设置窗口要"取消即丢弃"，所以必须有独立的草稿副本；
    /// 手写 Clone 迟早会漏字段，JSON 往返则永远和字段同步。
    /// </summary>
    public UniSearchSettings Clone()
        => JsonSerializer.Deserialize<UniSearchSettings>(SettingsStore.Serialize(this), SettingsStore.JsonOptions)
           ?? new UniSearchSettings();

    /// <summary>把越界/无意义的值拉回可用范围。加载后与保存前都跑，保证内存里和盘上都是干净的。</summary>
    public void Normalize()
    {
        Hotkeys ??= new HotkeySettings();
        Search ??= new SearchSettings();
        Preview ??= new PreviewSettings();
        Window ??= new WindowSettings();
        Columns ??= new ColumnsSettings();
        Archive ??= new ArchiveSettings();
        Providers ??= new(StringComparer.OrdinalIgnoreCase);

        Hotkeys.Normalize();
        Search.Normalize();
        Columns.Normalize();
        Archive.Normalize();
        Version = CurrentVersion;
    }

    public ProviderSettings ForProvider(string id)
    {
        if (!Providers.TryGetValue(id, out var s))
        {
            s = new ProviderSettings();
            Providers[id] = s;
        }
        return s;
    }
}

/// <summary>全局热键。字符串用 WPF 的显示串（<c>Alt+Windows+Space</c>），实测可无损往返。</summary>
public sealed class HotkeySettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>全局唤出（不动别人的程序）。</summary>
    public string Summon { get; set; } = "Alt+Windows+Space";

    /// <summary>
    /// 唤出并限定到前台资源管理器的当前目录。
    /// <b>它会全局抢走资源管理器原本的 Ctrl+F</b>，属于侵入性行为，所以单独可关。
    /// </summary>
    public string DirectoryScope { get; set; } = "Ctrl+F";

    public const string DefaultSummon = "Alt+Windows+Space";
    public const string DefaultDirectoryScope = "Ctrl+F";

    public void Normalize()
    {
        // 空串 = 有意不注册这个热键（用户在设置里按了 Delete 清掉），保留原样。
        // 非空但解析不了 = 用户敲错或手改坏了，退回默认，免得留一个永远不会生效的串。
        if (!string.IsNullOrWhiteSpace(Summon) && !Gestures.IsValid(Summon)) Summon = DefaultSummon;
        if (!string.IsNullOrWhiteSpace(DirectoryScope) && !Gestures.IsValid(DirectoryScope)) DirectoryScope = DefaultDirectoryScope;

        // 两个键撞在一起时保留唤出键：它不抢别人的东西，优先级更高
        if (Enabled && Summon.Length > 0
            && string.Equals(Summon, DirectoryScope, StringComparison.OrdinalIgnoreCase))
            DirectoryScope = DefaultDirectoryScope == Summon ? string.Empty : DefaultDirectoryScope;
    }
}

/// <summary>搜索行为。</summary>
public sealed class SearchSettings
{
    /// <summary>
    /// 列表最多显示多少行 —— 平铺单表视图下的<b>总行数上限</b>。
    /// <para>
    /// 取代了分组视图时代的"每类 24 行"（<c>perCategoryLimit</c>）：单表平铺之后没有"每组"，
    /// 那个上限只会让每一类各自被砍到 24 条，用户看到的是"明明有 102 个 pdf 只显示 24 个"。
    /// 老文件里的旧键会在载入时迁移到这里，然后从文件里消失（见 <see cref="Normalize"/>）。
    /// </para>
    /// </summary>
    public int MaxRows { get; set; } = DefaultMaxRows;

    /// <summary>
    /// 随输入<b>自动</b>参与搜索的后端 id。默认只有 <c>everything</c>。
    /// <para>
    /// 其它后端要用户在左侧来源栏点它才搜 —— 这是刻意的：全文检索类后端（AnyTXT 之类）
    /// 有真实开销，不该在每次敲键盘时都被拉起来。左栏点选只影响本次会话，改默认集合要来这里。
    /// </para>
    /// </summary>
    public List<string> AutoSearchProviders { get; set; } = ["everything"];

    /// <summary>默认过滤隐藏/系统文件。</summary>
    public bool FilterHiddenAndSystem { get; set; } = true;

    /// <summary>排除系统噪声位置（回收站 / WinSxS / servicing / Windows.old）。</summary>
    public bool ExcludeNoisePaths { get; set; } = true;

    /// <summary>用户自己加的排除目录（Everything 的 <c>!path:"…"</c> 语义）。</summary>
    public List<string> ExtraExcludePaths { get; set; } = [];

    public const int DefaultMaxRows = 500;

    // ── v1 遗留：分组视图时代的每类上限 ──────────────────────────
    // 保留只为"读得进老文件"，Normalize 迁移完即置空，之后不再写回（WhenWritingNull）。

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PerCategoryLimit { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PerCategoryLimitInFocus { get; set; }

    public void Normalize()
    {
        // v1 → v2 迁移：旧的"单分类上限"（200）就是用户心里那个"我想看到更多"的数字，
        // 拿它当新的列表上限，比直接扔掉、退回默认值更接近他的预期。
        var legacy = PerCategoryLimitInFocus ?? PerCategoryLimit;
        if (legacy is int v && v > 0) MaxRows = v;
        PerCategoryLimit = null;
        PerCategoryLimitInFocus = null;

        MaxRows = Math.Clamp(MaxRows, 20, 20000);

        AutoSearchProviders ??= [];
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new List<string>();
        foreach (var raw in AutoSearchProviders)
        {
            var id = (raw ?? string.Empty).Trim().ToLowerInvariant();
            if (id.Length == 0 || !seenIds.Add(id)) continue;
            ids.Add(id);
        }
        AutoSearchProviders = ids;

        ExtraExcludePaths ??= [];
        // 去空、去重（大小写不敏感）、去尾部反斜杠：同一个目录不该因为写法不同被排除两次
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var clean = new List<string>();
        foreach (var raw in ExtraExcludePaths)
        {
            var p = (raw ?? string.Empty).Trim().TrimEnd('\\', '/');
            if (p.Length == 0) continue;
            if (!seen.Add(p)) continue;
            clean.Add(p);
        }
        ExtraExcludePaths = clean;
    }
}

/// <summary>
/// 结果表的列布局。<b>纯 UI 状态，但一样落盘</b> —— 用户拖好的列宽、点出来的排序，
/// 下次启动应该还在；否则每次开机都要重调一遍，等于没有这个功能。
/// </summary>
public sealed class ColumnsSettings
{
    /// <summary>可见列，<b>数组顺序就是显示顺序</b>；没出现在这里的列 = 隐藏。</summary>
    public List<string> Visible { get; set; } = ["name", "path", "size", "modified", "kind"];

    /// <summary>列宽（列 key → 像素）。没记录的列用列目录里的默认宽度。</summary>
    public Dictionary<string, int> Widths { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>排序键（<c>relevance</c> = 不按列排，用相关度）。取值见 Core 的 ResultSort。</summary>
    public string SortKey { get; set; } = "name";

    public bool SortDescending { get; set; }

    public void Normalize()
    {
        Visible ??= [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var raw in Visible)
        {
            var k = (raw ?? string.Empty).Trim().ToLowerInvariant();
            if (k.Length == 0 || !seen.Add(k)) continue;
            list.Add(k);
        }
        Visible = list;

        Widths ??= new(StringComparer.OrdinalIgnoreCase);
        foreach (var key in Widths.Keys.ToList())
        {
            var w = Widths[key];
            if (w <= 0) { Widths.Remove(key); continue; }
            Widths[key] = Math.Clamp(w, 40, 2000);
        }

        SortKey = string.IsNullOrWhiteSpace(SortKey) ? "name" : SortKey.Trim().ToLowerInvariant();
    }
}

public sealed class PreviewSettings
{
    /// <summary>启动/唤出时预览窗格是否默认展开（运行中仍可用 Ctrl+J 随时开合）。</summary>
    public bool OpenByDefault { get; set; } = true;
}

public sealed class WindowSettings
{
    /// <summary>
    /// 点 X 时收进托盘（true）还是直接退出程序（false）。
    /// 收进托盘是启动器的常规行为，但必须有可见的退出入口（托盘右键菜单里有）。
    /// </summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>第一次收进托盘时弹一次气泡提示。</summary>
    public bool NotifyOnFirstHide { get; set; } = true;

    /// <summary>
    /// 开机自启动（HKCU Run）。默认关；设置保存后即时写注册表。
    /// </summary>
    public bool AutoStartAtLogin { get; set; }
}

/// <summary>单个 Provider 的配置节。</summary>
public sealed class ProviderSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Provider 私有键值（扁平字符串，Provider 自己解释）。</summary>
    public Dictionary<string, string> Options { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>热键串的解析/校验/显示。</summary>
public static class Gestures
{
    static readonly System.Windows.Input.KeyGestureConverter Converter = new();

    /// <summary>能不能解析成合法手势。校验用，避免把用户敲错的串写进配置。</summary>
    public static bool IsValid(string? gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture)) return false;
        try { return Converter.ConvertFromInvariantString(gesture) is System.Windows.Input.KeyGesture; }
        catch { return false; }
    }

    /// <summary>解析成 WPF 手势；失败返回 null（调用方按"没配"处理，不要让程序起不来）。</summary>
    public static System.Windows.Input.KeyGesture? Parse(string? gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture)) return null;
        try { return Converter.ConvertFromInvariantString(gesture) as System.Windows.Input.KeyGesture; }
        catch { return null; }
    }

    /// <summary>规范化为显示串（<c>win+alt+space</c> → <c>Alt+Windows+Space</c>）。</summary>
    public static string? Normalize(string? gesture)
    {
        var g = Parse(gesture);
        return g is null ? null : Converter.ConvertToInvariantString(g);
    }

    /// <summary>
    /// 从当前键盘状态生成手势串（设置窗口的按键捕获用）。
    /// <b>必须要求至少一个修饰键</b>：不然注册一个 <c>F</c> 就把全系统的 F 键抢了。
    /// </summary>
    public static string? FromKeyboard(System.Windows.Input.Key key, System.Windows.Input.ModifierKeys mods)
    {
        // Alt 组合键在 WPF 里 key 报的是 Key.System，真键在 SystemKey —— 由调用方归一化后传进来
        if (key is System.Windows.Input.Key.None
            or System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl
            or System.Windows.Input.Key.LeftAlt or System.Windows.Input.Key.RightAlt
            or System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift
            or System.Windows.Input.Key.LWin or System.Windows.Input.Key.RWin
            or System.Windows.Input.Key.System)
            return null;

        // 只按功能键（F1..F24）允许不带修饰键，其余一律要求修饰键
        var isFunctionKey = key is >= System.Windows.Input.Key.F1 and <= System.Windows.Input.Key.F24;
        if (mods == System.Windows.Input.ModifierKeys.None && !isFunctionKey) return null;

        try
        {
            return Converter.ConvertToInvariantString(new System.Windows.Input.KeyGesture(key, mods));
        }
        catch { return null; }
    }
}

/// <summary>
/// 多选压缩的设置（第 12 轮）。默认值刻意保守且可预期：包放"第一个选中项所在目录"、
/// 名字带时间戳、完成后在资源管理器里定位 —— 都不打断"选完就压"的顺手感。
/// <para>
/// 之所以做成设置而不是写死：落点与命名是最容易"因人和因场景而异"的两件事
/// （有人要每次都问、有人要固定目录；有人喜欢纯名字、有人要带时间戳）。
/// </para>
/// </summary>
public sealed class ArchiveSettings
{
    /// <summary><c>same-as-first</c>（默认：放第一个选中项所在目录）或 <c>ask</c>（每次弹保存对话框）。</summary>
    public string Destination { get; set; } = "same-as-first";

    /// <summary>包名模板。变量：<c>{parent}</c> <c>{count}</c> <c>{yyyyMMdd-HHmm}</c> <c>{yyyyMMdd}</c>。</summary>
    public string NameTemplate { get; set; } = "{parent}-{count}项-{yyyyMMdd-HHmm}";

    /// <summary>压缩完成后在资源管理器里定位这个包（关掉就只是静默完成）。</summary>
    public bool RevealAfter { get; set; } = true;

    /// <summary>一次最多压多少项（超过先提示，防手滑压 500 个）。<c>0</c> = 不限制。</summary>
    public int MaxItems { get; set; } = 200;

    public void Normalize()
    {
        if (Destination is not ("ask" or "same-as-first")) Destination = "same-as-first";
        if (string.IsNullOrWhiteSpace(NameTemplate)) NameTemplate = "{parent}-{count}项-{yyyyMMdd-HHmm}";
        if (MaxItems < 0) MaxItems = 0;
    }
}
