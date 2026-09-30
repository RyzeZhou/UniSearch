using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Win32;
using UniSearch.Sdk.Capabilities;
using UniSearch.Sdk.Contracts;
using UniSearch.Sdk.Model;
using UniSearch.Sdk.Runtime;

namespace UniSearch.Providers.Zotero;

/// <summary>
/// 第一方 Provider：Zotero 10 的本地 API。
/// <para>
/// <b>它补的是"知识库"这一格</b>：Everything 看文件名、AnyTXT 看正文，而 Zotero 看的是
/// <b>元数据</b>（标题 / 作者 / 年份 / 期刊 / 标签 / 集合），以及 PDF 正文（<c>qmode=everything</c>）。
/// </para>
/// <para>
/// <b>刻意不实现 <see cref="IFileSystemScopedProvider"/></b>：Zotero 没有目录概念，
/// 而调度器对"不实现它的 Provider"的处理正是"在 Explorer 目录内搜索时跳过它" ——
/// 那就是我们要的语义（在 D:\x 里找文件时不该问 Zotero）。集合限定走
/// <see cref="SearchContext.NamedScope"/>（<c>zotero:collection/&lt;KEY&gt;</c>）。
/// </para>
/// <para>
/// <b>刻意不声明 <see cref="ProviderCapability.SupportsKindFilter"/></b>：调度器把它当成
/// "能吃 ext: 过滤"，而 Zotero 根本不按扩展名筛（实测）。声明了它，<c>ext:pdf</c> 就会把
/// Zotero 拉进来跑一趟、再把结果全丢掉 —— 白费一次调用还污染来源栏状态。
/// 按"条目类型"筛仍然有效，那走的是 <c>ReturnsBibliographicItems</c> 这些能力位。
/// </para>
/// </summary>
[UniSearchProvider("zotero", "Zotero", ApiVersion = 1, RequiredApp = "Zotero",
    InstallHint = "https://www.zotero.org/download/")]
public sealed class ZoteroProvider : ISearchProvider, IGlobalScopeProvider, IActionProvider,
                                     IExternalUiProvider, IFacetProvider
{
    public const string ProviderId = "zotero";

    /// <summary>标签值域的 id。<b>与 Zotero 的查询参数同名</b>（<c>tag=</c>）—— 这不是巧合，
    /// 少一层"域 id → 参数名"的映射，就少一处两边的名字能对不上的地方。</summary>
    public const string TagFacetId = "tag";

    /// <summary>集合范围的标识前缀（与 SDK 文档里的 <c>zotero:collection/&lt;KEY&gt;</c> 一致）。</summary>
    public const string CollectionScopePrefix = "zotero:collection/";

    readonly ZoteroApiClient _client;
    readonly SemaphoreSlim _gate = new(1, 1);

    IProviderRuntime? _runtime;
    string? _version;

    public ZoteroProvider(string? host = null) => _client = new ZoteroApiClient(host);

    /// <summary>查询选项（由宿主从 <c>providers.zotero.options</c> 推送）。</summary>
    public ZoteroQueryOptions Options { get; private set; } = ZoteroQueryOptions.Default;

    public ProviderDescriptor Descriptor { get; private set; } = new()
    {
        Id = ProviderId,
        DisplayName = "Zotero",
        Version = "1.0.0",
        Description = "文献库：标题 / 作者 / 年份 / 期刊 / 标签 / 集合，以及 PDF 正文",
        // 慢后端：要起进程内 HTTP 服务、要读 SQLite。排在文件索引类之后。
        Priority = 30,
        LatencyHint = TimeSpan.FromMilliseconds(800),
        DependsOn = new ExternalDependency
        {
            Name = "Zotero",
            ProbeKind = DependencyProbeKind.HttpEndpoint,
            ProbeValue = ZoteroApiClient.DefaultHost + ZoteroApiClient.PingPath,
            Required = true,
            InstallUrl = "https://www.zotero.org/download/",
        },
        Capabilities =
            ProviderCapability.ReturnsBibliographicItems | ProviderCapability.ReturnsNotes |
            ProviderCapability.ReturnsAttachments |
            ProviderCapability.SearchesMetadata | ProviderCapability.SearchesFileContent |
            ProviderCapability.SupportsGlobalScope | ProviderCapability.ProvidesActions,
        // B4：左栏真图标（见 ProviderIcon 注释 —— 线索而非写死路径）
        Icon = new ProviderIcon("Zotero", "zotero.exe", "", ZoteroLocator.FindExecutable()),
    };

    public ValueTask InitializeAsync(IProviderRuntime runtime, CancellationToken ct)
    {
        _runtime = runtime;

        if (runtime.Settings.TryGetValue("baseUrl", out var url) && !string.IsNullOrWhiteSpace(url))
            _runtime?.Log.Info(ProviderId, $"基地址被设置覆盖：{url}");

        if (runtime.Settings.TryGetValue("qmode", out var qmode) &&
            qmode is "everything" or "titleCreatorYear")
        {
            Options = Options with { Qmode = qmode };
            _runtime?.Log.Info(ProviderId, $"qmode = {qmode}");
        }

        return default;
    }

    /// <summary>
    /// 更新查询选项。宿主推强类型对象，而不是让 Provider 自己去读 settings.json ——
    /// 用户看到的是"要不要连 PDF 正文一起搜"这种人话开关，Provider 不该知道设置文件的形状。
    /// </summary>
    public void ApplyOptions(ZoteroQueryOptions options)
    {
        Options = options;
        _runtime?.Log.Info(ProviderId, $"查询选项已更新：qmode={options.Qmode}");
    }

    // ───────────────────────── 健康 ─────────────────────────

    public async ValueTask<ProviderHealth> ProbeHealthAsync(CancellationToken ct)
    {
        if (!await _client.PingAsync(ct).ConfigureAwait(false))
            return ProviderHealth.Down(ProviderId, "Zotero 未在运行",
                "启动 Zotero；本地 API 只在它运行期间存在");

        // ping 通不代表 API 可用 —— 用户可能没勾"允许本机其他应用与 Zotero 通信"，
        // 那时所有 /api/ 请求都是 403。分开报，否则用户会一直去重启 Zotero。
        var probe = await _client.ProbeApiAsync(ct).ConfigureAwait(false);
        if (!probe.Available)
            return ProviderHealth.Down(ProviderId, probe.Error ?? "本地 API 不可用", probe.Hint);

        _version = probe.Version;
        return ProviderHealth.Ok(ProviderId, _version);
    }

    // ───────────────────────── 调度契约 ─────────────────────────

    public bool EnabledForGlobalScope(SearchContext context) => true;

    // ───────────────────────── 值域筛选（标签） ─────────────────────────

    /// <summary>
    /// 只有"标签"这一个值域。Zotero 的字段有 40 种条目类型 × 246 个字段，但<b>能筛的只有三个</b>
    /// （类型 / 标签 / 集合，实测见 ZOTERO-LOCAL-API-VERIFIED.md §3）：类型已经由标签栏的
    /// "条目类型"筛选器走 <c>itemType=</c> 下推，集合是范围不是筛选，所以这里只声明标签。
    /// </summary>
    public IReadOnlyList<FacetDescriptor> Facets { get; } =
    [
        new(TagFacetId, "标签", "\uE8EC", MatchAllDefault: false,
            Tip: "候选标签来自 Zotero 自己的标签库。多选默认「任一命中」—— 按「全部命中」时两个冷门标签的交集常常是 0 条。"),
    ];

    public async ValueTask<FacetValues> GetFacetValuesAsync(string facetId, SearchContext context, CancellationToken ct)
    {
        if (!string.Equals(facetId, TagFacetId, StringComparison.OrdinalIgnoreCase))
            return FacetValues.Failed(facetId, $"Zotero 没有「{facetId}」这个值域");

        ZoteroTagList tags;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            tags = await _client.GetTagsAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        if (!tags.Ok)
        {
            _runtime?.Log.Warn(ProviderId, $"读取标签库失败：{tags.Error}");
            return FacetValues.Failed(facetId, tags.Error ?? "读取标签库失败");
        }

        _runtime?.Log.Debug(ProviderId, $"标签库 {tags.Tags.Count} 个");
        return new FacetValues(facetId, tags.Tags.Select(t => new FacetValue(t.Name, t.Count)).ToList());
    }

    // ───────────────────────── 搜索 ─────────────────────────

    public async IAsyncEnumerable<SearchBatch> SearchAsync(
        SearchQuery query, SearchContext context,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // limit 必须显式给：本地 API 省略 limit 会一次返回整个文库（实测）。
        var limit = Math.Clamp(query.ResultBudget, 1, 200);
        var collectionKey = ParseCollectionKey(context.NamedScope);

        var translation = ZoteroQueryTranslator.Translate(query, limit, collectionKey, Options);
        foreach (var note in translation.Notes)
            _runtime?.Log.Info(ProviderId, "降级说明：" + note);

        ZoteroResponse response;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            response = await _client.GetAsync(translation.Request.Path, translation.Request.Query, ct)
                                    .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        if (!response.Ok)
        {
            _runtime?.Log.Warn(ProviderId, $"查询失败：{response.Error}{(response.Hint is null ? "" : " · " + response.Hint)}");
            yield return SearchBatch.Empty(ProviderId, query.RequestId);
            yield break;
        }

        var rows = ZoteroItemMapper.MapArray(response.Root, response.Total);
        _runtime?.Log.Debug(ProviderId,
            $"{translation.Request.Path} q={translation.Request.Q ?? "-"} qmode={translation.Request.Qmode ?? "-"} " +
            $"itemType={translation.Request.ItemType ?? "-"} " +
            $"tag={(translation.Request.Tags.Count == 0 ? "-" : string.Join(" ; ", translation.Request.Tags))} " +
            $"-> {rows.Count} 行（总数 {response.Total?.ToString() ?? "?"}）");

        yield return SearchBatch.Of(ProviderId, query.RequestId, rows, response.Total, isLast: true);
    }

    /// <summary>
    /// 从 <c>NamedScope</c> 里取集合 key。只认自己那一套前缀 —— 别的后端的 scope 不该被误解析。
    /// </summary>
    internal static string? ParseCollectionKey(string? namedScope)
        => namedScope is { Length: > 0 } s && s.StartsWith(CollectionScopePrefix, StringComparison.OrdinalIgnoreCase)
            ? s[CollectionScopePrefix.Length..].Trim()
            : null;

    // ───────────────────────── 动作 ─────────────────────────

    /// <summary>
    /// "在 Zotero 中打开" —— 这是 Zotero 最该有的动作：搜到了就直接跳过去看/编辑。
    /// URI 形式 <c>zotero://select/library/items/&lt;KEY&gt;</c> 由 Zotero 自己注册的协议处理。
    /// </summary>
    public IReadOnlyList<ResultAction> GetActions(SearchResult result)
        => result.ProviderId == ProviderId && result.ProviderItemId.Length > 0
            ? [new ResultAction { Id = "zotero.open", Label = "在 Zotero 中打开", Glyph = "\uE8A7" }]
            : [];

    public ValueTask<ActionResult> ExecuteAsync(ResultAction action, SearchResult result, CancellationToken ct)
    {
        if (action.Id != "zotero.open") return ValueTask.FromResult(ActionResult.NotHandled());

        var uri = ZoteroItemMapper.SelectUri(result.ProviderItemId);
        var ok = _runtime?.Process.StartUri(uri) ?? false;
        return ValueTask.FromResult(ok
            ? ActionResult.Ok()
            : ActionResult.Fail($"唤不起 Zotero（{uri}）—— 确认它已安装并注册了 zotero:// 协议"));
    }

    // ───────────────────────── 跳到后端自己的界面 ─────────────────────────

    public string ExternalUiName => "Zotero";

    /// <summary>跳转入口 = exe，退而求其次开始菜单 .lnk / App Paths。</summary>
    string? ResolveLaunchTarget() => ZoteroLocator.FindExecutable()
        ?? ShellAppLocator.FindStartMenuShortcut("Zotero")
        ?? ShellAppLocator.FindViaAppPaths("zotero.exe");

    public bool CanOpenExternalUi => ResolveLaunchTarget() is not null;

    /// <summary>
    /// 把 Zotero 带到前台。
    /// <para>
    /// <b>它没有"把查询传进去"的口子</b> —— <c>zotero://</c> 协议只支持定位到某个对象
    /// （<c>select/library/items/&lt;KEY&gt;</c>），没有搜索形式；命令行也只有 <c>-url</c>。
    /// 所以这里如实只做"唤起"，真正的"跳过去看这一条"是每条结果上的
    /// <see cref="GetActions"/> 动作。**不假装传了查询。**
    /// </para>
    /// </summary>
    public bool OpenExternalUi(string query)
    {
        // exe 找不到时退到开始菜单快捷方式 / App Paths（ShellExecute 对 .lnk 解引用目标）
        var exe = ResolveLaunchTarget();
        if (exe is null)
        {
            _runtime?.Log.Warn(ProviderId, "找不到 Zotero 的入口（exe / 快捷方式 / App Paths 都没有），无法跳转");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(query))
            _runtime?.Log.Info(ProviderId, "Zotero 没有接收查询的命令行/URI，只把它带到前台（要定位某条请用结果上的「在 Zotero 中打开」）");

        var ok = _runtime?.Process.OpenFile(exe) ?? false;
        if (!ok) _runtime?.Log.Warn(ProviderId, $"唤起 Zotero 失败：{exe}");
        return ok;
    }

    public ValueTask ShutdownAsync(CancellationToken ct)
    {
        _client.Dispose();
        _gate.Dispose();
        return default;
    }
}

/// <summary>
/// 找 Zotero 装在哪。<c>zotero://</c> 协议注册表项里就写着可执行文件路径，
/// 那是权威来源；硬编码路径只是兜底。
/// </summary>
public static class ZoteroLocator
{
    public const string ExeName = "zotero.exe";

    static string? _cached;
    static bool _probed;

    public static string? FindExecutable()
    {
        if (_probed) return _cached;
        _probed = true;
        _cached = Probe();
        return _cached;
    }

    public static string? Probe()
    {
        foreach (var candidate in Candidates())
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                return candidate;
        return null;
    }

    public static IEnumerable<string> Candidates()
    {
        // ① 协议注册（本机实测：HKEY_LOCAL_MACHINE\Software\Classes\zotero\shell\open\command
        //    = "D:\Program\Zotero\zotero.exe" -url "%1"）
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            string? fromReg = null;
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
                using var cmd = baseKey.OpenSubKey(@"Software\Classes\zotero\shell\open\command");
                if (cmd?.GetValue(null) is string line)
                    fromReg = ExtractExe(line);
            }
            catch
            {
                // 读不动就跳过这一支
            }
            if (fromReg is not null) yield return fromReg;
        }

        // ② 常见安装位置
        yield return @"C:\Program Files\Zotero\zotero.exe";
        yield return @"C:\Program Files (x86)\Zotero\zotero.exe";
        yield return @"D:\Program\Zotero\zotero.exe";
        yield return @"D:\Program Files\Zotero\zotero.exe";
    }

    /// <summary>从 <c>"D:\Program\Zotero\zotero.exe" -url "%1"</c> 里取出可执行文件路径。</summary>
    internal static string? ExtractExe(string commandLine)
    {
        var s = commandLine.Trim();
        if (s.Length == 0) return null;

        if (s[0] == '"')
        {
            var end = s.IndexOf('"', 1);
            return end > 1 ? s[1..end] : null;
        }

        var space = s.IndexOf(' ');
        return space > 0 ? s[..space] : s;
    }
}
