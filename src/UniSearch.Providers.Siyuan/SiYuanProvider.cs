using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Win32;
using UniSearch.Sdk.Capabilities;
using UniSearch.Sdk.Contracts;
using UniSearch.Sdk.Model;
using UniSearch.Sdk.Runtime;

namespace UniSearch.Providers.Siyuan;

/// <summary>
/// 第一方 Provider：思源笔记内核 HTTP API（知识库<b>块级</b>检索）。
/// <para>
/// <b>它补的是"自己的笔记"这一格</b>：Everything 看文件名、AnyTXT 看磁盘上的正文、
/// Zotero 看文献元数据，而思源看的是<b>散在笔记本里的块</b> —— 段落、标题、代码块、列表项。
/// 这些内容在磁盘上只是 <c>.sy</c>（JSON）文件，用 Everything/AnyTXT 搜等于搜 JSON 源码。
/// </para>
/// <para>
/// <b>部署形态是跨机的</b>：思源跑在宿主机（192.168.200.1:6806），UniSearch 跑在 VM 上。
/// 因此 <c>host</c> / <c>port</c> / <c>token</c> 三个选项都是必需的（本机场景留空即可），
/// 且<b>不做</b> <c>siyuan://</c> 协议唤起为唯一路径 —— VM 上没注册那个协议。
/// </para>
/// </summary>
[UniSearchProvider("siyuan", "思源笔记", ApiVersion = 1, RequiredApp = "SiYuan",
    InstallHint = "https://b3log.org/siyuan/")]
public sealed class SiYuanProvider : ISearchProvider, IGlobalScopeProvider, IActionProvider,
                                    IExternalUiProvider, IFacetProvider, IPreviewProvider
{
    public const string ProviderId = "siyuan";

    /// <summary>笔记本作用域的标识前缀（与 <see cref="SearchContext.NamedScope"/> 拼法一致）。</summary>
    public const string NotebookScopePrefix = "siyuan:nb/";

    /// <summary>值域 id：笔记本。与查询参数同名（<c>box</c>）—— 少一层映射就少一处能对不上的地方。</summary>
    public const string NotebookFacetId = "nb";

    readonly SemaphoreSlim _gate = new(1, 1);

    SiYuanApiClient _client;
    IProviderRuntime? _runtime;
    string? _version;

    Dictionary<string, string> _notebookNames = new(StringComparer.OrdinalIgnoreCase);
    DateTimeOffset _notebookNamesAt = DateTimeOffset.MinValue;

    public SiYuanProvider() => _client = new SiYuanApiClient();

    /// <summary>连接选项（由宿主从 <c>providers.siyuan.options</c> 推送）。</summary>
    public SiYuanQueryOptions Options { get; private set; } = SiYuanQueryOptions.Default;

    public ProviderDescriptor Descriptor { get; private set; } = new()
    {
        Id = ProviderId,
        DisplayName = "思源笔记",
        Version = "1.0.0",
        Description = "知识库笔记块：段落 / 标题 / 代码块 / 列表项的正文检索",
        // 局域网 HTTP，比本地文件索引慢、比 Zotero 快
        Priority = 40,
        LatencyHint = TimeSpan.FromMilliseconds(200),
        DependsOn = new ExternalDependency
        {
            Name = "SiYuan",
            ProbeKind = DependencyProbeKind.HttpEndpoint,
            // 探活端点免鉴权（实测跨机也免），所以没配凭证时健康判断依然是准的
            ProbeValue = SiYuanApiClient.DefaultHost + ":" + SiYuanApiClient.DefaultPort + SiYuanApiClient.VersionPath,
            Required = true,
            InstallUrl = "https://b3log.org/siyuan/",
        },
        // ⚠ 刻意**不声明** SupportsKindFilter：调度器把它当成"能吃 ext: 过滤"，
        // 而思源的块根本没有扩展名（实测）—— 声明了，ext:pdf 就会把它拉进来跑一趟再全丢掉。
        // 这是从 Zotero 那边学来的同一条教训。
        // 也刻意不声明 ReturnsFiles：否则 kind:code 之类会把一个"没有文件"的后端算成候选。
        Capabilities =
            ProviderCapability.ReturnsNotes | ProviderCapability.ReturnsDocuments |
            ProviderCapability.SearchesFileContent |
            ProviderCapability.SupportsGlobalScope | ProviderCapability.ProvidesActions,
    };

    // ───────────────────────── 连接 ─────────────────────────

    public ValueTask InitializeAsync(IProviderRuntime runtime, CancellationToken ct)
    {
        _runtime = runtime;
        ApplyOptions(ReadOptions(runtime.Settings));
        return default;
    }

    /// <summary>
    /// 从设置节读连接参数。三个键都可空 —— 空 = 本机默认（<c>127.0.0.1:6806</c>、免鉴权）。
    /// </summary>
    internal static SiYuanQueryOptions ReadOptions(IReadOnlyDictionary<string, string> settings)
    {
        var host = settings.TryGetValue("host", out var h) && !string.IsNullOrWhiteSpace(h)
            ? h.Trim() : SiYuanApiClient.DefaultHost;

        var port = settings.TryGetValue("port", out var p) && int.TryParse(p, out var n) && n > 0
            ? n : SiYuanApiClient.DefaultPort;

        // 凭证只在这里过一手：不落日志、不入库、不写进任何文档
        var token = settings.TryGetValue("token", out var t) && !string.IsNullOrWhiteSpace(t) ? t.Trim() : null;

        return new SiYuanQueryOptions { Host = host, Port = port, Token = token };
    }

    /// <summary>
    /// 换连接参数 = 换一个 client。旧的要 Dispose，否则每改一次设置泄漏一个连接池。
    /// </summary>
    public void ApplyOptions(SiYuanQueryOptions options)
    {
        if (options == Options) return;
        Options = options;

        var old = _client;
        _client = new SiYuanApiClient(options.Host, options.Port, options.Token);
        old?.Dispose();

        _notebookNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _notebookNamesAt = DateTimeOffset.MinValue;
        _runtime?.Log.Info(ProviderId, $"连接已更新：{_client.BaseUrl}（凭证：{(options.Token is null ? "无" : "已配置")}）");
    }

    // ───────────────────────── 健康 ─────────────────────────

    public async ValueTask<ProviderHealth> ProbeHealthAsync(CancellationToken ct)
    {
        var probe = await _client.ProbeAsync(ct).ConfigureAwait(false);
        if (!probe.Available)
            return ProviderHealth.Down(ProviderId, probe.Error ?? "思源内核不可达",
                probe.Hint ?? "启动思源笔记；跨机时确认 host/port 填对");

        _version = probe.Version;
        return ProviderHealth.Ok(ProviderId, _version);
    }

    // ───────────────────────── 调度契约 ─────────────────────────

    public bool EnabledForGlobalScope(SearchContext context) => true;

    /// <summary>
    /// 从 <c>NamedScope</c> 取笔记本 id。只认自己那一套前缀 —— 别的后端的 scope 不该被误解析。
    /// </summary>
    internal static string? ParseNotebookScope(string? namedScope)
        => namedScope is { Length: > 0 } s && s.StartsWith(NotebookScopePrefix, StringComparison.OrdinalIgnoreCase)
            ? s[NotebookScopePrefix.Length..].Trim()
            : null;

    // ───────────────────────── 搜索 ─────────────────────────

    public async IAsyncEnumerable<SearchBatch> SearchAsync(
        SearchQuery query, SearchContext context,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var translation = SiYuanQueryTranslator.Translate(query, ParseNotebookScope(context.NamedScope));
        foreach (var note in translation.Notes)
            _runtime?.Log.Info(ProviderId, "降级说明：" + note);

        var statement = translation.Request.Statement;
        string? error = null;
        string? hint = null;
        JsonElement rows = default;
        var total = 0;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 先要总数（状态条要显示"共 N 条"），再要这一页。
            // 两次请求而不是一次：局域网 HTTP 毫秒级，而"显示一个错的总数"是实打实的误导。
            var countResp = await _client.QuerySqlAsync(translation.Request.CountStatement, ct).ConfigureAwait(false);
            if (countResp.Ok && countResp.Data.ValueKind == JsonValueKind.Array &&
                countResp.Data.GetArrayLength() > 0 &&
                countResp.Data[0].TryGetProperty("n", out var n) && n.TryGetInt32(out var totalN))
                total = totalN;

            var resp = await _client.QuerySqlAsync(statement, ct).ConfigureAwait(false);
            if (resp.Ok)
            {
                rows = resp.Data;
                // 计数查询失败时退回行数（不理想，但总好过显示 0）
                if (total == 0 && resp.Data.ValueKind == JsonValueKind.Array)
                    total = resp.Data.GetArrayLength();
            }
            else
            {
                error = resp.Error;
                hint = resp.Hint;
            }
        }
        finally
        {
            _gate.Release();
        }

        if (error is not null)
        {
            // 如实报错，不返回空结果假装"没搜到" —— 凭证不对与笔记里确实没这个词，
            // 在界面上必须分得开（这是本项目反复出现的同一类坑）
            _runtime?.Log.Warn(ProviderId, $"查询失败：{error}{(hint is null ? "" : " · " + hint)}");
            yield return SearchBatch.Empty(ProviderId, query.RequestId);
            yield break;
        }

        var names = await NotebookNamesAsync(ct).ConfigureAwait(false);
        var mapped = SiYuanItemMapper.MapArray(rows, total, names);

        _runtime?.Log.Debug(ProviderId, $"{Summarize(statement)} -> {mapped.Count} 行");

        yield return SearchBatch.Of(ProviderId, query.RequestId, mapped, total, isLast: true);
    }

    /// <summary>日志里不要整条 SQL（长），只留条件部分 —— 一眼看出筛了什么。</summary>
    internal static string Summarize(string statement)
    {
        const string marker = " FROM blocks";
        var idx = statement.IndexOf(marker, StringComparison.Ordinal);
        var rest = idx >= 0 ? statement[(idx + marker.Length)..] : statement;
        return rest.Length > 160 ? rest[..160] + "…" : rest;
    }

    /// <summary>笔记本 id → 名字（Subtitle 与值域面板都要用）。60 秒缓存：改名了也不至于一直错。</summary>
    async Task<IReadOnlyDictionary<string, string>> NotebookNamesAsync(CancellationToken ct)
    {
        if (_notebookNames.Count > 0 && DateTimeOffset.UtcNow - _notebookNamesAt < TimeSpan.FromSeconds(60))
            return _notebookNames;

        var map = await FetchNotebooksAsync(ct).ConfigureAwait(false);
        if (map is not null)
        {
            _notebookNames = map;
            _notebookNamesAt = DateTimeOffset.UtcNow;
        }
        return _notebookNames;
    }

    async Task<Dictionary<string, string>?> FetchNotebooksAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var resp = await _client.ListNotebooksAsync(ct).ConfigureAwait(false);
            if (!resp.Ok) return null;
            if (!resp.Data.TryGetProperty("notebooks", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return null;

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var nb in arr.EnumerateArray())
            {
                if (nb.ValueKind != JsonValueKind.Object) continue;
                if (Str(nb, "id") is not { Length: > 0 } id) continue;
                map[id] = Str(nb, "name") ?? id;
            }
            return map;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ───────────────────────── 值域（笔记本）─────────────────────────

    /// <summary>
    /// 笔记本是唯一的值域。块类型（段落/标题/代码块…）也有"候选值"的意思，但那一层
    /// 已经由<b>内置分类</b>承担（Kind = 文档/笔记/代码），再做一个值域只会两处打架。
    /// </summary>
    public IReadOnlyList<FacetDescriptor> Facets { get; } =
    [
        new(NotebookFacetId, "笔记本", "\uE8B7", MatchAllDefault: false,
            Tip: "候选笔记本来自思源自己。多选恒为「任一」—— 一个块只属于一个笔记本。"),
    ];

    public async ValueTask<FacetValues> GetFacetValuesAsync(string facetId, SearchContext context, CancellationToken ct)
    {
        if (!string.Equals(facetId, NotebookFacetId, StringComparison.OrdinalIgnoreCase))
            return FacetValues.Failed(facetId, $"思源没有「{facetId}」这个值域");

        var names = await NotebookNamesAsync(ct).ConfigureAwait(false);
        if (names.Count == 0)
            return FacetValues.Failed(facetId, "读不到笔记本清单（确认思源在运行、凭证有效）");

        // 每个笔记本的块数：只有 SQL 给得出（lsNotebooks 不返回计数）。
        // 拿不到就退回 0 —— 面板上不显示数字，总好过显示一个错的。
        var counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var resp = await _client.QuerySqlAsync(
                "SELECT box, COUNT(*) AS n FROM blocks GROUP BY box", ct).ConfigureAwait(false);
            if (resp.Ok && resp.Data.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in resp.Data.EnumerateArray())
                {
                    if (Str(row, "box") is not { Length: > 0 } box) continue;
                    if (row.TryGetProperty("n", out var n) && n.TryGetInt64(out var v)) counts[box] = v;
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        // 保持思源自己的顺序（lsNotebooks 按 sort）—— 与桌面端左侧列表一致。
        // ⚠ 显示名是笔记本<b>名字</b>，下推值必须是 <b>id</b>（SQL 里 box 存的是 id）。
        //   第一版把名字当成了下推值，box IN ('R语言') 永远 0 条（实测踩到）。
        var list = names.Select(kv => new FacetValue(kv.Value, counts.TryGetValue(kv.Key, out var c) ? c : 0, kv.Key))
                        .ToList();
        _runtime?.Log.Debug(ProviderId, $"笔记本值域 {list.Count} 个");
        return new FacetValues(facetId, list);
    }

    /// <summary>笔记本名 → id（自检与内部用；名字重名时取先出现的那个）。</summary>
    public IReadOnlyDictionary<string, string> NotebookIdsByName()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in _notebookNames)
            map.TryAdd(kv.Value, kv.Key);
        return map;
    }

    // ───────────────────────── 动作 ─────────────────────────

    public IReadOnlyList<ResultAction> GetActions(SearchResult result)
    {
        if (result.ProviderId != ProviderId || result.ProviderItemId.Length == 0) return [];

        var actions = new List<ResultAction>();
        if (HasProtocol())
            actions.Add(new ResultAction { Id = "siyuan.open", Label = "在思源中打开", Glyph = "\uE8A7" });
        actions.Add(new ResultAction { Id = "siyuan.copylink", Label = "复制 siyuan:// 链接", Glyph = "\uE8C8" });
        actions.Add(new ResultAction { Id = "siyuan.export", Label = "导出 Markdown", Glyph = "\uE8B7" });
        return actions;
    }

    public async ValueTask<ActionResult> ExecuteAsync(ResultAction action, SearchResult result, CancellationToken ct)
    {
        if (result.ProviderId != ProviderId) return ActionResult.NotHandled();
        var id = result.ProviderItemId;

        switch (action.Id)
        {
            case "siyuan.open":
            {
                var uri = SiYuanItemMapper.BlockUri(id);
                var ok = _runtime?.Process.StartUri(uri) ?? false;
                return ok
                    ? ActionResult.Ok()
                    : ActionResult.Fail($"唤不起思源（{uri}）—— 本机没有注册 siyuan:// 协议；跨机部署时用「导出 Markdown」");
            }

            case "siyuan.copylink":
            {
                // 跨机部署的"跳过去"就靠它：VM 上没有 siyuan:// 协议，把链接复制出来，
                // 到装了思源的那台机器上（或远程桌面里）粘贴打开
                var uri = SiYuanItemMapper.BlockUri(id);
                var ok = _runtime?.Process.CopyToClipboard(uri) ?? false;
                return ok
                    ? ActionResult.Ok()
                    : ActionResult.Fail("复制失败：宿主没有提供剪贴板能力");
            }

            case "siyuan.export":
            {
                var resp = await _client.ExportMdAsync(id, ct).ConfigureAwait(false);
                if (!resp.Ok)
                    return ActionResult.Fail($"导出失败：{resp.Error}{(resp.Hint is null ? "" : " · " + resp.Hint)}");
                if (!resp.Data.TryGetProperty("content", out var contentEl) ||
                    contentEl.ValueKind != JsonValueKind.String)
                    return ActionResult.Fail("导出返回里没有 content");

                var dir = Path.Combine(_runtime?.DataDirectory ?? Path.GetTempPath(), "siyuan-export");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, Sanitize(id) + ".md");
                await File.WriteAllTextAsync(file, contentEl.GetString(), ct).ConfigureAwait(false);

                var opened = _runtime?.Process.OpenFile(file) ?? false;
                return opened
                    ? ActionResult.Ok()
                    : ActionResult.Fail($"已导出到 {file}，但打不开它（文件管理器里手动打开）");
            }

            default:
                return ActionResult.NotHandled();
        }
    }

    /// <summary>块 id 是 <c>20251016171654-s0twauh</c> 这种形状，但仍要过一道白名单再当文件名。</summary>
    internal static string Sanitize(string id)
    {
        var chars = id.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray();
        return chars.Length > 0 ? new string(chars) : "block";
    }

    // ───────────────────────── 预览（跨机 Markdown）─────────────────────────

    /// <summary>
    /// 块的预览内容：<c>exportMdContent</c> 把块导成带 frontmatter 的 Markdown，直接给文本面板。
    /// <para>
    /// <b>这是第一个跨机预览</b>：块没有本地文件，磁盘上根本没有可读的东西 —— 之前
    /// <c>DisablePreview=true</c> 是"如实说没有"；现在改为把内容<b>取过来</b>。
    /// Markdown 以源码形态显示，与磁盘上的 .md 文件走同一条"文本预览"路，不自研渲染器。
    /// </para>
    /// </summary>
    public async ValueTask<PreviewContent?> GetPreviewAsync(SearchResult result, PreviewRequest request, CancellationToken ct)
    {
        if (result.ProviderId != ProviderId || result.ProviderItemId.Length == 0) return null;

        var resp = await _client.ExportMdAsync(result.ProviderItemId, ct).ConfigureAwait(false);
        if (!resp.Ok)
        {
            _runtime?.Log.Warn(ProviderId, $"预览失败：{resp.Error}{(resp.Hint is null ? "" : " · " + resp.Hint)}");
            return null;
        }

        var hPath = Str(resp.Data, "hPath");
        var content = Str(resp.Data, "content");
        if (string.IsNullOrEmpty(content))
        {
            _runtime?.Log.Warn(ProviderId, "预览返回里没有 content（块可能已被删除）");
            return null;
        }

        return PreviewContent.AsText(hPath ?? result.Title, TruncateForPreview(content!));
    }

    /// <summary>
    /// 预览正文上限。Sdk 约定 ≤64KB；这里收敛到与本地文本预览同一量级 ——
    /// 预览面板是小窗，一篇长笔记的后半截基本不会有人翻到。
    /// </summary>
    internal static string TruncateForPreview(string content, int maxChars = 6000)
    {
        if (content.Length <= maxChars) return content;
        return content[..maxChars] + "\n\n…（已截断，动作里有「导出 Markdown」可看全文）";
    }

    // ───────────────────────── 跳到后端自己的界面 ─────────────────────────

    public string ExternalUiName => "思源笔记";

    /// <summary>
    /// 只有当<b>本机</b>注册了 <c>siyuan://</c> 协议时才可用。跨机部署（UniSearch 在 VM、
    /// 思源在宿主机）时这里是 false —— 如实说不可用，好过点了没反应。
    /// </summary>
    public bool CanOpenExternalUi => HasProtocol();

    public bool OpenExternalUi(string query)
    {
        // 思源没有"传查询串唤起"的官方入口（不像 Everything 的 -s、AnyTXT 的 /s），
        // 所以只把窗口带起来，不假装能把查询送过去。
        if (SiYuanLocator.FindExecutable() is { Length: > 0 } exe)
            return _runtime?.Process.OpenFile(exe) ?? false;
        return _runtime?.Process.StartUri("siyuan://") ?? false;
    }

    // ───────────────────────── 协议探测 ─────────────────────────

    static bool _protocolProbed;
    static bool _hasProtocol;

    /// <summary>本机是否注册了 <c>siyuan://</c>（决定"在思源中打开"这个动作出不出现）。</summary>
    internal static bool HasProtocol()
    {
        if (_protocolProbed) return _hasProtocol;
        _protocolProbed = true;
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(@"siyuan\shell\open\command");
            _hasProtocol = key?.GetValue(null) is string line && line.Length > 0;
        }
        catch
        {
            _hasProtocol = false;
        }
        return _hasProtocol;
    }

    /// <summary>仅供测试重置静态探测缓存。</summary>
    internal static void ResetProtocolProbeForTests()
    {
        _protocolProbed = false;
        _hasProtocol = false;
    }

    public ValueTask ShutdownAsync(CancellationToken ct)
    {
        _client.Dispose();
        return default;
    }

    static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>思源的连接选项（<c>providers.siyuan.options</c>）。</summary>
public sealed record SiYuanQueryOptions
{
    public string Host { get; init; } = SiYuanApiClient.DefaultHost;
    public int Port { get; init; } = SiYuanApiClient.DefaultPort;

    /// <summary>跨机访问的凭证。<b>只存在设置文件与内存里</b>，不进日志、不入库、不写文档。</summary>
    public string? Token { get; init; }

    public static SiYuanQueryOptions Default { get; } = new();
}

/// <summary>定位本机的思源可执行文件（只用于"跳到后端界面"这个可选动作）。</summary>
public static class SiYuanLocator
{
    public const string ExeName = "SiYuan.exe";

    static string? _cached;
    static bool _probed;

    public static string? FindExecutable()
    {
        if (_probed) return _cached;
        _probed = true;
        foreach (var candidate in Candidates())
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                {
                    _cached = candidate;
                    break;
                }
            }
            catch
            {
                // 路径不合法就跳过
            }
        }
        return _cached;
    }

    public static IEnumerable<string> Candidates()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                  "Programs", "SiYuan", ExeName);
        yield return @"C:\Program Files\SiYuan\" + ExeName;
        yield return @"D:\Program Files\SiYuan\" + ExeName;
        yield return @"D:\SiYuan\" + ExeName;
    }
}
