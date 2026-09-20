using System.Runtime.CompilerServices;
using System.Threading.Channels;
using EverythingSearchClient;
using UniSearch.Sdk.Capabilities;
using UniSearch.Sdk.Contracts;
using UniSearch.Sdk.Model;
using UniSearch.Sdk.Runtime;
using ESC = EverythingSearchClient;

namespace UniSearch.Providers.Everything;

/// <summary>
/// 第一方 Provider：Everything 文件索引。
/// 传输交给 <c>EverythingSearchClient</c>（Apache-2.0，纯托管 WM_COPYDATA，已实现 QUERY2：
/// 能拿到 Size / 创建与修改时间 / FileAttributes，并支持 SortBy 与忙时策略），
/// 本类只负责：语义翻译、结果归一化、能力声明、健康探测。
/// </summary>
[UniSearchProvider("everything", "Everything", ApiVersion = 1, RequiredApp = "Everything",
    InstallHint = "https://www.voidtools.com/downloads/")]
public sealed class EverythingProvider : ISearchProvider, IGlobalScopeProvider, IFileSystemScopedProvider,
                                        IDirectoryListProvider, IActionProvider, IExternalUiProvider
{
    public const string ProviderId = "everything";

    readonly ESC.SearchClient _client = new() { ReceiveTimeout = TimeSpan.FromMilliseconds(1500) };

    /// <summary>Everything 每个 reply 窗口同时只跑一个查询，因此这里串行化；上层的新请求取消会直接打断等待。</summary>
    readonly SemaphoreSlim _gate = new(1, 1);

    IProviderRuntime? _runtime;

    /// <summary>Everything.exe 路径，惰性定位（便携安装没有注册表项，靠 IPC 宿主进程反查）。</summary>
    string? _exePath;
    bool _exeProbed;

    public ProviderDescriptor Descriptor { get; private set; } = new()
    {
        Id = ProviderId,
        DisplayName = "Everything",
        Version = "1.0.0",
        Description = "NTFS / 目录索引的文件名搜索，毫秒级返回全量结果",
        Priority = 100,
        LatencyHint = TimeSpan.FromMilliseconds(30),
        DependsOn = new ExternalDependency
        {
            Name = "Everything",
            ProbeKind = DependencyProbeKind.WindowClass,
            ProbeValue = EverythingLocator.IpcWindowClass,
            Required = true,
            InstallUrl = "https://www.voidtools.com/downloads/",
        },
        Capabilities =
            ProviderCapability.ReturnsFiles | ProviderCapability.ReturnsFolders |
            ProviderCapability.ReturnsImages | ProviderCapability.ReturnsVideos |
            ProviderCapability.ReturnsAudio | ProviderCapability.ReturnsDocuments |
            ProviderCapability.ReturnsApplications | ProviderCapability.ReturnsArchives |
            ProviderCapability.SearchesFileName | ProviderCapability.SupportsKindFilter |
            ProviderCapability.SupportsDirectoryScope | ProviderCapability.SupportsGlobalScope |
            ProviderCapability.ProvidesActions | ProviderCapability.SupportsListing,
    };

    public ValueTask InitializeAsync(IProviderRuntime runtime, CancellationToken ct)
    {
        _runtime = runtime;
        var v = EverythingLocator.Probe();
        if (v.Version is not null) Descriptor = Descriptor with { Version = v.Version.ToString() };
        return default;
    }

    /// <summary>当前的查询翻译选项（由宿主从用户设置的 <c>search.*</c> 推送过来）。</summary>
    public EverythingQueryOptions Options { get; private set; } = EverythingQueryOptions.Default;

    /// <summary>
    /// 更新查询选项。<b>宿主推强类型对象，而不是让 Provider 自己去读 settings.json</b>：
    /// 用户看到的是"排除系统噪声"这种人话开关，翻译器要的是路径列表，这层映射只该有一处
    /// （在宿主里）；Provider 不该知道设置文件的形状。改完下一次查询即刻生效，不用重启。
    /// </summary>
    public void ApplyOptions(EverythingQueryOptions options)
    {
        Options = options;
        _runtime?.Log.Info("everything",
            $"查询选项已更新：排除噪声={options.ExcludeNoisePaths}，额外排除 {options.ExtraExcludePaths.Count} 条");
    }

    public ValueTask<ProviderHealth> ProbeHealthAsync(CancellationToken ct)
    {
        try
        {
            if (!ESC.SearchClient.IsEverythingAvailable())
                return ValueTask.FromResult(ProviderHealth.Down(ProviderId,
                    "Everything 未在运行", "启动 Everything，或在其中开启“开机自启/作为服务运行”"));
            return ValueTask.FromResult(ProviderHealth.Ok(ProviderId, ESC.SearchClient.GetEverythingVersion()?.ToString()));
        }
        catch (Exception ex)
        {
            return ValueTask.FromResult(ProviderHealth.Down(ProviderId, ex.Message));
        }
    }

    // ───────────────────────── 调度契约 ─────────────────────────

    public bool EnabledForGlobalScope(SearchContext context) => true;

    /// <summary>能限定目录：翻译成一个 <c>ancestor:"…\"</c> / <c>parent:"…\"</c> 前缀，交给 Everything 自己收敛。</summary>
    public string? TranslateScope(SearchContext context) =>
        context.IsDirectoryBounded ? EverythingQueryTranslator.QuotePath(context.RootPath!) : null;

    // ───────────────────────── 跳到后端自己的界面 ─────────────────────────

    public string ExternalUiName => "Everything";

    public bool CanOpenExternalUi => ResolveExe() is not null;

    /// <summary>
    /// 把当前查询带到 Everything 自己的窗口。
    /// <para>
    /// 命令行开关是**实测**的（不是照文档猜的）：<c>everything.exe -search "&lt;q&gt;"</c>
    /// 会让已在运行的实例把标题变成 <c>&lt;q&gt; - Everything</c>，即真的执行了搜索，且复用同一进程。
    /// </para>
    /// </summary>
    public bool OpenExternalUi(string query)
    {
        var exe = ResolveExe();
        if (exe is null)
        {
            _runtime?.Log.Warn(ProviderId, "找不到 Everything.exe，无法跳转到它的界面");
            return false;
        }

        // 引号会破坏 Everything 的命令行解析，直接去掉（搜索串里本来也不该有）
        var args = string.IsNullOrWhiteSpace(query)
            ? string.Empty
            : "-search \"" + query.Replace("\"", string.Empty) + "\"";

        var ok = _runtime?.Process.OpenFile(exe, args) ?? false;
        if (!ok) _runtime?.Log.Warn(ProviderId, $"唤起 Everything 失败：{exe} {args}");
        return ok;
    }

    string? ResolveExe()
    {
        if (!_exeProbed)
        {
            _exePath = EverythingLocator.FindExecutable();
            _exeProbed = true;
            if (_exePath is not null) _runtime?.Log.Info(ProviderId, $"Everything.exe = {_exePath}");
        }
        return _exePath;
    }

    // ───────────────────────── 搜索 ─────────────────────────

    public async IAsyncEnumerable<SearchBatch> SearchAsync(
        SearchQuery query, SearchContext context,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var search = EverythingQueryTranslator.Translate(query, context, Options);
        if (search.Length == 0)
        {
            yield return SearchBatch.Empty(ProviderId, query.RequestId);
            yield break;
        }

        // 第一页小一点保证首屏；第二页补足“其他”分区
        var pages = new (uint offset, uint size)[] { (0, (uint)Math.Clamp(query.ResultBudget, 20, 200)),
                                                     ((uint)Math.Clamp(query.ResultBudget, 20, 200), 200) };

        for (var i = 0; i < pages.Length; i++)
        {
            ESC.Result res;
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                res = await Task.Run(() => _client.Search(
                    search,
                    (ESC.SearchClient.SearchFlags)MapFlags(query),
                    pages[i].size,
                    pages[i].offset,
                    ESC.SearchClient.BehaviorWhenBusy.Continue,
                    (uint)query.Deadline.TotalMilliseconds,
                    ESC.SearchClient.SortBy.Name,
                    ESC.SearchClient.SortDirection.Ascending), ct).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }

            var mapped = new List<SearchResult>(res.Items.Length);
            foreach (var item in res.Items)
            {
                ct.ThrowIfCancellationRequested();
                mapped.Add(Map(item, query, res.TotalItems));
            }

            yield return SearchBatch.Of(ProviderId, query.RequestId, mapped, (int)res.TotalItems,
                isLast: i == pages.Length - 1 || mapped.Count == 0);

            if (mapped.Count == 0) yield break;
        }
    }

    public IAsyncEnumerable<SearchBatch> ListAsync(string directory, long requestId, CancellationToken ct)
    {
        var ctx = SearchContext.InDirectory(directory, QueryOrigin.ExplorerHotkey, recursive: false);
        var q = new SearchQuery { RequestId = requestId, RawText = "", ListScopeContents = true, ResultBudget = 100 };
        return SearchAsync(q, ctx, ct);
    }

    static ESC.SearchClient.SearchFlags MapFlags(SearchQuery q)
    {
        var t = EverythingQueryTranslator.BuildFlags(q);
        var f = ESC.SearchClient.SearchFlags.None;
        if (t.HasFlag(EverythingSearchFlags.RegEx)) f |= ESC.SearchClient.SearchFlags.RegEx;
        if (t.HasFlag(EverythingSearchFlags.MatchCase)) f |= ESC.SearchClient.SearchFlags.MatchCase;
        if (t.HasFlag(EverythingSearchFlags.MatchWholeWord)) f |= ESC.SearchClient.SearchFlags.MatchWholeWord;
        return f;
    }

    /// <summary>把 Everything 的 Item 归一化成统一结果模型。<b>不猜分类</b>，只声明 Kind/Subtype/Match。</summary>
    internal static SearchResult Map(ESC.Result.Item item, SearchQuery q, uint total)
    {
        var full = CombinePath(item.Path, item.Name);
        var isFolder = item.Flags is ESC.Result.ItemFlags.Folder or ESC.Result.ItemFlags.Drive;
        var ext = Canonicalization.GetExtension(full);
        var pathOriented = q.Filters.Kinds.Count == 0 && EverythingProviderHelpers.PathOnlyMatch(q, full);

        var kind = isFolder ? ResultKind.Folder
            : ext is not null && EverythingQueryTranslator.Picture.Contains(ext) ? ResultKind.Image
            : ext is not null && EverythingQueryTranslator.Video.Contains(ext) ? ResultKind.Video
            : ext is not null && EverythingQueryTranslator.Audio.Contains(ext) ? ResultKind.Audio
            : ext is not null && EverythingQueryTranslator.Executable.Contains(ext) ? ResultKind.Application
            : ResultKind.File;


        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        if (item.FileAttributes is { } fa) meta["attributes"] = ((int)fa).ToString();
        if (item.LastWriteTime is { } lw) meta["dateModified"] = lw.ToString("o");
        if (item.CreationTime is { } cr) meta["dateCreated"] = cr.ToString("o");

        return new SearchResult
        {
            ProviderId = ProviderId,
            ProviderItemId = full,
            Kind = kind,
            Subtype = ext,
            // 命中质量由 Core 的 Ranker 统一评估：Provider 只声明“这是名称/路径命中”，不猜分数
            Match = pathOriented ? MatchKind.Path : MatchKind.NameWord,
            Title = item.Name,
            Subtitle = item.Path,
            AutoCompleteText = item.Name,
            Path = full,
            SizeBytes = (long?)item.Size,
            ModifiedAt = item.LastWriteTime is { } m ? new DateTimeOffset(m) : null,
            CreatedAt = item.CreationTime is { } c ? new DateTimeOffset(c) : null,
            Metadata = meta,
            TotalAvailable = (int)total,
            Icon = new IconHint { ShellIconPath = full, Badge = ext?.ToUpperInvariant() },
            Tags = [],
            Payload = full,
        };
    }

    /// <summary>Everything 的 path 字段是否带结尾分隔符在不同版本上不一致，这里统一。</summary>
    internal static string CombinePath(string? path, string name)
    {
        if (string.IsNullOrEmpty(path)) return name;
        return path[^1] == Canonicalization.Sep ? path + name : path + Canonicalization.Sep + name;
    }

    // ───────────────────────── 动作 ─────────────────────────

    public IReadOnlyList<ResultAction> GetActions(SearchResult result) => [];   // 通用动作由宿主统一注入

    public ValueTask<ActionResult> ExecuteAsync(ResultAction action, SearchResult result, CancellationToken ct)
        => ValueTask.FromResult(ActionResult.NotHandled());

    public ValueTask ShutdownAsync(CancellationToken ct)
    {
        _gate.Dispose();
        return default;
    }
}

// 局部判据：查询词是否只在路径里出现（决定 MatchKind 是 Path 还是 NameWord）。
// 有意做得保守 —— 真正的命中质量评估在 Core.Ranking，不在这里。
static partial class EverythingProviderHelpers
{
    public static bool PathOnlyMatch(UniSearch.Sdk.Model.SearchQuery q, string full)
    {
        if (q.Terms.Count == 0) return false;
        var name = UniSearch.Sdk.Model.Canonicalization.GetFileName(full);
        foreach (var t in q.Terms)
        {
            if (name.Contains(t, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return full.Contains(q.MatchTarget, StringComparison.OrdinalIgnoreCase);
    }
}
