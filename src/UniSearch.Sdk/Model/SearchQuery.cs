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

    /// <summary>
    /// <b>值域</b>筛选（候选值来自后端自己，见 <c>IFacetProvider</c>）。空 = 不限。
    /// <para>
    /// 与 <see cref="Extensions"/> / <see cref="Kinds"/> 的区别：那几个的值域由<b>宿主</b>定义
    /// （扩展名表、类型枚举），这里由<b>后端</b>定义 —— Zotero 有哪些标签只有它自己知道。
    /// </para>
    /// <para>
    /// 能下推的后端（Zotero <c>tag=</c>）会把它翻进查询串，此时结果的"共 N 条"是服务端口径；
    /// 不能下推的后端不该被调度（ProviderSelector 会跳过），刻意不做前端近似过滤 ——
    /// 值域候选来自后端、结果却在别处近似匹配，两边对不上的时候用户只会看到莫名其妙的空列表。
    /// </para>
    /// </summary>
    public IReadOnlyList<FacetSelection> Facets { get; init; } = [];

    /// <summary>
    /// 有没有<b>真的选了值</b>的值域。空壳（域在、值空）不算 —— UI 允许留一个没勾任何值的空壳
    /// （用户刚把最后一个勾去掉的那一刻），那不该被当成"要筛"。
    /// <para>判断"要不要为值域改变调度"一律用这个，不要直接看 <see cref="Facets"/>.Count。</para>
    /// </summary>
    public bool HasFacetSelection
    {
        get
        {
            foreach (var f in Facets)
                if (f.Values.Count > 0) return true;
            return false;
        }
    }

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
        Extensions.Count == 0 && Kinds.Count == 0 && Subtypes.Count == 0 && !HasFacetSelection &&
        Phrase is null && !FoldersOnly &&
        !RegexRequested && MinSizeBytes is null && MaxSizeBytes is null && ModifiedAfter is null &&
        ExplicitDirectory is null;
}

/// <summary>
/// 值域筛选的一条选择。例如 <c>("tag", ["蛋白设计", "小分子"], MatchAll: false)</c>。
/// </summary>
/// <param name="FacetId">值域 id，由 <c>IFacetProvider.Facets</c> 声明（后端私有词汇，如 <c>tag</c>）。</param>
/// <param name="Values">选中的值。空集合 = 这一域没有选择（与"不出现"等价，UI 可以留着空壳）。</param>
/// <param name="MatchAll">
/// 多值时是"全部满足"（AND）还是"任一满足"（OR）。默认 OR —— 真按 AND 走的时候，
/// 两个冷门标签的交集常常是 0 条，用户会以为筛选坏了（实测 Zotero「蛋白设计」+「小分子」= 0）。
/// </param>
public sealed record FacetSelection(string FacetId, IReadOnlyList<string> Values, bool MatchAll = false);
