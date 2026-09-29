namespace UniSearch.Sdk.Contracts;

using UniSearch.Sdk.Capabilities;
using UniSearch.Sdk.Model;

// 能力接口彼此独立。Provider 实现多少决定它能参与哪些上下文，
// 这就是“Explorer 目录内搜索自动跳过 Zotero”的机制来源。

/// <summary>可在全局范围（Scope=Global）被调度。绝大多数 Provider 应实现。</summary>
public interface IGlobalScopeProvider
{
    /// <summary>在全局快捷键下是否启用（用户可能只想要文件结果）。</summary>
    bool EnabledForGlobalScope(SearchContext context) => true;
}

/// <summary>能把搜索限定到某个目录。<b>Broker 在 Explorer 上下文中只调度实现了此接口的 Provider。</b></summary>
public interface IFileSystemScopedProvider
{
    /// <summary>该 Provider 是否接受这一具体上下文（例如只支持递归、不支持仅本层时可返回 false）。</summary>
    bool CanScopeTo(SearchContext context) =>
        context.Scope is ScopeKind.CurrentDirectoryOnly
            or ScopeKind.CurrentDirectoryRecursive
            or ScopeKind.Selection;

    /// <summary>
    /// 把上下文翻译成 Provider 私有的范围表达式（如 Everything 的 <c>parent:"D:\x" </c>、
    /// AnyTXT 的 <c>filterDir</c>）。返回 null 表示“无法原生限定”，Broker 会改为对结果做后过滤。
    /// </summary>
    string? TranslateScope(SearchContext context) => null;
}

/// <summary>会搜文件正文，并能给出命中片段。UI 的“内容命中”分区据此存在。</summary>
public interface IContentSearchProvider
{
    /// <summary>该后端能全文检索的扩展名（小写、不含点）。空集合表示未知/太宽泛。</summary>
    IReadOnlyCollection<string> IndexedExtensions { get; }

    /// <summary>是否已建立索引（未建索引时 Broker 应给出“索引中 37%”的提示）。</summary>
    ValueTask<IndexState> GetIndexStateAsync(CancellationToken ct);
}

public sealed record IndexState(bool Indexed, long TotalItems, long IndexedItems, string? Phase)
{
    public double Progress => TotalItems <= 0 ? (Indexed ? 1 : 0) : (double)IndexedItems / TotalItems;
    public static IndexState Unknown() => new(false, 0, 0, null);
    public static IndexState Complete(long n) => new(true, n, n, "idle");
}

/// <summary>提供右侧预览面板内容。</summary>
public interface IPreviewProvider
{
    ValueTask<PreviewContent?> GetPreviewAsync(SearchResult result, PreviewRequest request, CancellationToken ct);
}

/// <summary>提供右键菜单 / Ctrl+Enter 之类的动作。打开文件、打开所在目录等通用动作由宿主统一提供，此处只声明<b>领域专属</b>动作。</summary>
public interface IActionProvider
{
    IReadOnlyList<ResultAction> GetActions(SearchResult result);
    ValueTask<ActionResult> ExecuteAsync(ResultAction action, SearchResult result, CancellationToken ct);
}

/// <summary>
/// 后端自带独立界面/程序，宿主据此提供"跳过去继续操作"的按钮。
/// <para>
/// 典型场景：在 UniSearch 里搜到东西后，想用后端自己的界面做后续动作
/// （Everything 的筛选/排序/导出、AnyTXT 的正文命中定位、Zotero 的条目管理）。
/// 这是**单向跳转**，不参与结果融合，也不改变本窗口的状态。
/// </para>
/// <para>
/// 查询串由宿主按"用户看得懂"的形式给出（即用户输入 + 可能的目录限定），
/// Provider 负责翻译成自己程序的命令行。空串表示只把后端窗口带到前台。
/// </para>
/// </summary>
public interface IExternalUiProvider
{
    /// <summary>按钮上显示的名字，例如 <c>"Everything"</c>。</summary>
    string ExternalUiName { get; }

    /// <summary>当前是否可跳转（程序没装时要能如实说"不可用"，而不是点了没反应）。</summary>
    bool CanOpenExternalUi { get; }

    /// <summary>用给定查询唤起后端界面；返回 false 表示失败（原因由 Provider 记日志）。</summary>
    bool OpenExternalUi(string query);
}

/// <summary>输入过程中的补全建议（不产生结果行，只喂自动补全条）。</summary>
public interface ISuggestionProvider
{
    IAsyncEnumerable<SuggestionGroup> SuggestAsync(string text, SearchContext context, CancellationToken ct);
}

/// <summary>能列举一个目录的内容（Explorer 空查询时显示“此文件夹内”）。</summary>
public interface IDirectoryListProvider
{
    IAsyncEnumerable<SearchBatch> ListAsync(string directory, long requestId, CancellationToken ct);
}

/// <summary>
/// 提供<b>值域筛选器</b>：候选值来自后端自己，而不是宿主写死的表。
/// <para>
/// 与 <see cref="IActionProvider"/> 那类能力不同，这个接口服务的是<b>过滤条</b>：
/// 宿主把 <see cref="Facets"/> 画成一枚可展开的筛选器（候选值多到摊不平，所以是"点开才展开"），
/// 展开时调 <see cref="GetFacetValuesAsync"/> 取全部候选值；用户勾选的结果进
/// <see cref="SearchQuery.Filters"/> 的 <c>Facets</c>，由 Provider 自己翻译成后端查询参数。
/// </para>
/// <para>
/// <b>为什么候选值必须来自后端</b>：Zotero 的标签库是用户随时在改的，宿主这边没有任何
/// 同步来源。写死一份列表，第一天就对不上了。
/// </para>
/// <para>
/// 声明了这个接口，就意味着<b>必须能把 Facets 下推进自己的查询</b>（宿主会据此调度：
/// 不支持的后端在带 Facets 的查询里会被直接跳过，见 ProviderSelector）。
/// </para>
/// </summary>
public interface IFacetProvider
{
    /// <summary>该后端支持的值域。空 = 没有（那就别实现这个接口）。</summary>
    IReadOnlyList<FacetDescriptor> Facets { get; }

    /// <summary>
    /// 取某一域的全部候选值。失败要如实回 <see cref="FacetValues.Error"/>，
    /// 不要回空列表假装"这个库里没有标签"。
    /// </summary>
    ValueTask<FacetValues> GetFacetValuesAsync(string facetId, SearchContext context, CancellationToken ct);
}

/// <param name="Id">值域 id（后端私有词汇，如 <c>tag</c>）。</param>
/// <param name="DisplayName">筛选器上显示的名字。</param>
/// <param name="Glyph">Segoe Fluent 字形。</param>
/// <param name="MatchAllDefault">多选默认是不是"全部满足"。默认 false（任一满足）。</param>
/// <param name="Tip">提示文本：说清这个域的语义与多值口径。</param>
public sealed record FacetDescriptor(string Id, string DisplayName, string Glyph,
                                     bool MatchAllDefault = false, string? Tip = null);

public sealed record FacetValues(string FacetId, IReadOnlyList<FacetValue> Values, string? Error = null)
{
    public static FacetValues Failed(string facetId, string error) => new(facetId, [], error);
    public bool Ok => Error is null;
}

/// <param name="Value">给人看的显示名。</param>
/// <param name="Count">该值下的条目数（后端给的，不是已返回子集的口径）。</param>
/// <param name="Key">
/// 下推给后端用的值。<c>null</c> = 与 <see cref="Value"/> 相同（大多数场合如此，例如 Zotero 的标签）。
/// <para>
/// <b>为什么显示名与下推值必须分开</b>：思源的笔记本，给人看的是名字（"R语言"），
/// 而 SQL 里要的是 id（<c>20251209154600-9kzscxv</c>）。第一版把名字当成下推值送过去，
/// 结果 <c>box IN ('R语言')</c> 永远 0 条 —— 界面上看不出哪里错了（实测踩到）。
/// </para>
/// </param>
public sealed record FacetValue(string Value, long Count, string? Key = null)
{
    /// <summary>真正送给后端的那个值。</summary>
    public string Pushdown => Key is { Length: > 0 } k ? k : Value;
}
