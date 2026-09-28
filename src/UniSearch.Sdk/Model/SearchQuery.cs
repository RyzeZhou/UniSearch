namespace UniSearch.Sdk.Model;

/// <summary>
/// 解析后的查询。
/// <para>
/// <b>两种消费方式并存</b>（来自 PowerToys CmdPal 的 AQS 直通规则，见 docs/research/REF-5）：
/// <list type="bullet">
/// <item><description><b>自由文本</b>：<see cref="Terms"/> 已拆好，Core 可以做统一后过滤与打分；</description></item>
/// <item><description><b>结构化查询</b>（<see cref="IsStructured"/> = true）：不要拆、不要重排，
/// 把 <see cref="ProviderText"/> 原样交给懂该语法的后端，Core 也<b>不得</b>再用词项做后过滤。</description></item>
/// </list>
/// </para>
/// </summary>
public sealed record SearchQuery
{
    /// <summary>本次请求的单调递增序号。Provider 必须丢弃比自己更旧的请求产出的结果。</summary>
    public required long RequestId { get; init; }

    /// <summary>用户原始输入（回填搜索框、显示用）。</summary>
    public required string RawText { get; init; }

    /// <summary>剥掉过滤器语法后的自由文本部分。结构化查询时等于 <see cref="RawText"/>。</summary>
    public string Text { get; init; } = "";

    /// <summary>
    /// 交给后端的查询串（不含范围前缀，范围由各 Provider 自己拼）。
    /// 结构化查询 = 原样透传；自由文本 = 由过滤器重建。
    /// </summary>
    public string ProviderText { get; init; } = "";

    /// <summary>输入看起来是后端自己的查询语法（函数、括号、通配、OR、裸路径…），不应拆解。</summary>
    public bool IsStructured { get; init; }

    /// <summary><see cref="Text"/> 按空白拆分的词项，全部小写。结构化查询时为空列表。</summary>
    public IReadOnlyList<string> Terms { get; init; } = [];

    public QueryFilters Filters { get; init; } = QueryFilters.None;

    /// <summary>单个 Provider 的结果预算（不是最终展示条数上限）。</summary>
    public int ResultBudget { get; init; } = 60;

    /// <summary>整个请求的墙钟截止；超时后该 Provider 被标记为 Timeout 并降级。</summary>
    public TimeSpan Deadline { get; init; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>为 true 时 Provider 应返回"当前目录列举"（即使 <see cref="Text"/> 为空）。</summary>
    public bool ListScopeContents { get; init; }

    /// <summary>UI 顶部分类标签被点击后置入，Core 做硬过滤、Provider 当提示。</summary>
    public string? ForcedCategory { get; init; }

    /// <summary>
    /// 只让这些 Provider 参与本次查询（null 或空 = 不限制）。
    /// <para>
    /// 来源栏选中某个后端时由 UI 置入。<b>语义是"只搜它"，不是"只显示它的结果"</b> ——
    /// "后端没跑"和"跑了但结果被过滤掉"必须能区分，否则将来接上 AnyTXT 这类有真实开销
    /// （要起服务、要读索引）的后端时，"没搜"会被误读成"没结果"。
    /// </para>
    /// </summary>
    public IReadOnlyList<string>? ProviderScope { get; init; }

    /// <summary><see cref="ProviderScope"/> 是否构成有效约束。</summary>
    public bool HasProviderScope => ProviderScope is { Count: > 0 };

    public bool IsBlank =>
        string.IsNullOrWhiteSpace(Text) && Filters.IsEmpty && !ListScopeContents && !IsStructured;

    /// <summary>用于名称匹配打分的目标串。</summary>
    public string MatchTarget => string.IsNullOrEmpty(Filters.Phrase) ? Text : Filters.Phrase;

    /// <summary>结构化查询时给 UI 的提示（"已按 Everything 语法原样搜索"）。</summary>
    public string? SyntaxNotice => IsStructured ? "raw-syntax" : null;
}

/// <summary>显式过滤器。<c>ext:</c>、<c>kind:</c>、<c>size:</c>、<c>dm:</c> 等语法的结构化结果。</summary>
public sealed record QueryFilters
{
    public static readonly QueryFilters None = new();

    /// <summary>只允许这些扩展名（小写、不含点）。空 = 不限。</summary>
    public IReadOnlyList<string> Extensions { get; init; } = [];

    /// <summary>只允许这些结果类型。空 = 不限。</summary>
    public IReadOnlyList<ResultKind> Kinds { get; init; } = [];

    /// <summary>
    /// 只允许这些语义<b>子</b>类型（<see cref="SearchResult.Subtype"/>，小写连字符：
    /// <c>journal-article</c>、<c>preprint</c>、<c>attachment</c>…）。空 = 不限。
    /// <para>
    /// 为什么和 <see cref="Kinds"/> 并存：Kinds 只有"文献条目"这一档粗粒度，
    /// 表达不了"只要期刊论文、不要预印本"。能下推的后端（Zotero 的 <c>itemType=</c>）就下推，
    /// 不能的由 Core 做后过滤。
    /// </para>
    /// </summary>
    public IReadOnlyList<string> Subtypes { get; init; } = [];

    /// <summary>用户用引号锁定的短语。</summary>
    public string? Phrase { get; init; }

    public bool FoldersOnly { get; init; }

    /// <summary>默认过滤隐藏与系统文件（Core 侧执行，Provider 只需回传 attributes 元数据）。</summary>
    public bool ExcludeHidden { get; init; } = true;

    public bool RegexRequested { get; init; }
    public bool WildcardRequested { get; init; }
    public string? RegexPattern { get; init; }
    public long? MinSizeBytes { get; init; }
    public long? MaxSizeBytes { get; init; }
    public DateTimeOffset? ModifiedAfter { get; init; }

    /// <summary>用户在查询里显式限定的目录（覆盖 Context.Scope 的唯一合法途径，需 UI 二次确认）。</summary>
    public string? ExplicitDirectory { get; init; }

    public bool IsEmpty =>
        Extensions.Count == 0 && Kinds.Count == 0 && Subtypes.Count == 0 && Phrase is null && !FoldersOnly &&
        !RegexRequested && MinSizeBytes is null && MaxSizeBytes is null && ModifiedAfter is null &&
        ExplicitDirectory is null;
}
