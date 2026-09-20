namespace UniSearch.Sdk.Model;

/// <summary>
/// 统一结果模型。<b>所有</b> Provider 的产出都必须能被无损表达为该记录 ——
/// 这是"前端不再为每个后端写特例"的前提。
/// </summary>
/// <remarks>
/// 设计约束（来自上游审计，docs/research/REF-2 §3）：
/// <list type="bullet">
/// <item><description><b>不允许</b>放委托、WPF 类型或任何不可序列化对象（Flow/CmdPal 都踩过：结果对象里塞
/// <c>Action</c> 与 <c>Lazy&lt;UserControl&gt;</c> 就无法跨进程、无法缓存、无法单测）。
/// Provider 私有的重对象放 <see cref="Payload"/>，但必须假定它只在同进程内有效。</description></item>
/// <item><description>不要求跨 Provider 可比的分数：<see cref="ProviderScore"/> 仅用于同一 Provider 内部排序，
/// 最终次序由 Core 的 Ranker 决定。</description></item>
/// </list>
/// </remarks>
public sealed record SearchResult
{
    /// <summary>产出该结果的 Provider id。</summary>
    public required string ProviderId { get; init; }

    /// <summary>Provider 内部稳定 id（Everything 的全路径、Zotero item key…）。同一查询内必须稳定。</summary>
    public required string ProviderItemId { get; init; }

    public required ResultKind Kind { get; init; }

    /// <summary>语义子类型，小写：<c>"pdf"</c>、<c>"journal-article"</c>、<c>"markdown-note"</c>。CategoryEngine 在 Kind 之后看它。</summary>
    public string? Subtype { get; init; }

    public MatchKind Match { get; init; } = MatchKind.NameWord;

    /// <summary>主标题：文件名 / 论文标题 / 笔记标题。</summary>
    public required string Title { get; init; }

    /// <summary>副标题：完整路径，或 "Vaswani et al. · 2017 · NeurIPS"。</summary>
    public string? Subtitle { get; init; }

    /// <summary>本地绝对路径（若对应磁盘对象）。<b>实体融合的锚点</b>，必须是完整路径。</summary>
    public string? Path { get; init; }

    /// <summary>非文件实体的打开目标：<c>zotero://select/library/items/KEY</c>、<c>obsidian://…</c>、<c>https://…</c>。</summary>
    public string? Uri { get; init; }

    /// <summary>正文命中摘要。命中词用 <c>[[</c> 与 <c>]]</c> 包裹（Everything 的 <c>*</c> 与 CmdPal 的 index 列表都会归一到这一记法）。</summary>
    public string? Snippet { get; init; }

    /// <summary><see cref="Title"/> 中需要高亮的字符区间（start,length）；null 表示由 UI 依据 <see cref="Snippet"/> 或匹配串自行推断。</summary>
    public IReadOnlyList<(int Start, int Length)>? TitleHighlight { get; init; }

    /// <summary>正文命中次数；null = 未知/不适用。</summary>
    public int? ContentMatchCount { get; init; }

    public long? SizeBytes { get; init; }
    public DateTimeOffset? ModifiedAt { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>扩展元数据：author / year / venue / tags / mime / attributes…（值一律字符串，便于跨进程）。</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = EmptyMetadata;

    /// <summary>回传给该 Provider 的私有对象（item key、原始响应行号）。Core 不解析、不序列化、不跨进程传递。</summary>
    public object? Payload { get; init; }

    /// <summary>Provider 自评相关度（仅同 Provider 内可比）。</summary>
    public double? ProviderScore { get; init; }

    /// <summary>后端总命中数，用于“还有 12,483 条”。Provider 可以在每条上重复填写同一个值。</summary>
    public int? TotalAvailable { get; init; }

    public IconHint? Icon { get; init; }

    /// <summary>结果行上的小标签（“Zotero”“12 处正文匹配”“离线”）。</summary>
    public IReadOnlyList<ResultTag> Tags { get; init; } = [];

    // ─────────── 交互细节（来自 Flow / CmdPal 的实践） ───────────

    /// <summary>Ctrl+C 实际复制的文本（可以是引用串/URL，而非标题）。null = 复制 <see cref="Path"/> 或 <see cref="Title"/>。</summary>
    public string? CopyText { get; init; }

    /// <summary>按 Tab 时把搜索框补全成什么（Everything 式 typeahead）。null = 不补全。</summary>
    public string? AutoCompleteText { get; init; }

    /// <summary>MRU 记账键。默认用 <see cref="FusionKey"/>；标题会变的实体（如"未命名笔记"）应显式给稳定键。</summary>
    public string? RecordKey { get; init; }

    /// <summary>是否参与"用户选过"计数（临时/建议类结果应设 false，避免污染排序）。</summary>
    public bool CountUsage { get; init; } = true;

    /// <summary>该条目禁用预览面板（超大文件、二进制、远端未缓存）。</summary>
    public bool DisablePreview { get; init; }

    /// <summary>该条目禁用"打开"动作（例如只读的远端结果，只能复制引用）。</summary>
    public bool ReadOnly { get; init; }

    public bool IsFolder => Kind == ResultKind.Folder;

    public static readonly IReadOnlyDictionary<string, string> EmptyMetadata =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>实体融合键：优先规范路径，其次 URI，最后 provider:id。</summary>
    public string FusionKey => Canonicalization.ToFusionKey(ProviderId, ProviderItemId, Path, Uri);

    /// <summary>MRU 实际用的键。</summary>
    public string UsageKey => RecordKey ?? FusionKey;

    /// <summary>该结果对应的文件扩展名（小写、不含点）；无则 null。</summary>
    public string? Extension => Path is null ? null : Canonicalization.GetExtension(Path);

    /// <summary>给 UI 的"这一行第二行显示什么"：优先副标题，其次目录。</summary>
    public string DisplaySecondary =>
        !string.IsNullOrEmpty(Subtitle) ? Subtitle!
        : Path is not null ? Canonicalization.GetDirectoryDisplayName(Path)
        : Uri ?? string.Empty;
}

/// <summary>结果行/预览面板上的小标签。</summary>
public sealed record ResultTag(string Label, ResultTagTone Tone = ResultTagTone.Neutral);

public enum ResultTagTone { Neutral, Info, Success, Warning, Danger, Accent }

/// <summary>图标获取提示。宿主的 IconService 负责缓存与 DPI（策略见 docs/research/REF-1 §2 的 IconProvider）。</summary>
public sealed record IconHint
{
    /// <summary>用于抽 shell 图标的路径（exe/dll/lnk/文件本身）。</summary>
    public string? ShellIconPath { get; init; }

    /// <summary>true = 只按扩展名要图标（用假文件名探测，不碰真实文件系统）。<b>列表滚动时必须走这条路</b>，否则网络盘会卡 UI。</summary>
    public bool IconByExtensionOnly { get; init; } = true;

    public int IconIndex { get; init; }

    /// <summary>直接可用的图片路径（缩略图、PDF 首页渲染缓存）。</summary>
    public string? ImagePath { get; init; }

    /// <summary>字体图标兜底（Segoe Fluent Icons 码点）。</summary>
    public string? Glyph { get; init; }

    /// <summary>纯色徽章文字，例如 PDF / DOCX。</summary>
    public string? Badge { get; init; }

    /// <summary>圆形裁切（头像、应用图标）。</summary>
    public bool Rounded { get; init; }
}
