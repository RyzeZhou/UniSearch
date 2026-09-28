using System.Runtime.CompilerServices;
using System.Text.Json;
using UniSearch.Sdk.Capabilities;
using UniSearch.Sdk.Contracts;
using UniSearch.Sdk.Model;
using UniSearch.Sdk.Runtime;

namespace UniSearch.Providers.Anytxt;

/// <summary>
/// 第一方 Provider：AnyTXT 全文内容搜索。
/// <para>
/// <b>它补的是 Everything 永远做不到的那一格</b>：按"文件里写了什么"找，而不是按文件名。
/// 因此 <see cref="SearchResult.Match"/> 一律标 <see cref="MatchKind.Content"/>。
/// </para>
/// <para>
/// 传输是本地 JSON-RPC（<c>http://127.0.0.1:9924/rpc</c>，方法命名空间 <c>anytxt.v1</c>）；
/// 本类只负责：语义翻译、结果归一化、能力声明、健康探测、盘符调度。
/// 协议事实见 <c>docs/research/ANYTXT-RPC-VERIFIED.md</c>。
/// </para>
/// </summary>
[UniSearchProvider("anytxt", "AnyTXT", ApiVersion = 1, RequiredApp = "Anytxt Searcher",
    InstallHint = "https://anytxt.net/")]
public sealed class AnytxtProvider : ISearchProvider, IGlobalScopeProvider, IFileSystemScopedProvider,
                                    IContentSearchProvider, IActionProvider, IExternalUiProvider
{
    public const string ProviderId = "anytxt";

    /// <summary>索引里能全文检索的扩展名 —— AnyTXT 覆盖面很宽，且随用户配置变，所以声明"未知"。</summary>
    static readonly string[] NoKnownExtensions = [];

    readonly AnytxtRpcClient _client;
    readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>上一次查询逐盘问到的行数（诊断用）。</summary>
    readonly List<(string Dir, int Rows)> _driveBreakdown = [];

    /// <summary>
    /// 上一次全盘查询的逐盘明细。
    /// <b>这是"跨盘合并真的发生了"唯一直接的证据</b> —— 只看合并后的行数，
    /// 没法区分"两个盘都问了"和"只问了 C 盘而 D 盘恰好没结果"。
    /// </summary>
    public IReadOnlyList<(string Dir, int Rows)> LastDriveBreakdown => _driveBreakdown;

    IProviderRuntime? _runtime;

    public AnytxtProvider(string? endpoint = null) => _client = new AnytxtRpcClient(endpoint);

    public ProviderDescriptor Descriptor { get; private set; } = new()
    {
        Id = ProviderId,
        DisplayName = "AnyTXT",
        Version = "1.0.0",
        Description = "全文内容搜索：按文件里写了什么找，而不是按文件名",
        // 慢后端：要读索引、可能跨盘。排在 Everything 之后，但比在线类靠前。
        Priority = 80,
        LatencyHint = TimeSpan.FromMilliseconds(500),
        DependsOn = new ExternalDependency
        {
            Name = "Anytxt Searcher",
            ProbeKind = DependencyProbeKind.HttpEndpoint,
            ProbeValue = AnytxtRpcClient.DefaultEndpoint,
            Required = true,
            InstallUrl = "https://anytxt.net/",
        },
        Capabilities =
            ProviderCapability.ReturnsFiles | ProviderCapability.ReturnsDocuments |
            ProviderCapability.SearchesFileContent | ProviderCapability.SupportsKindFilter |
            ProviderCapability.SupportsDirectoryScope | ProviderCapability.SupportsGlobalScope |
            ProviderCapability.ProvidesActions,
    };

    public ValueTask InitializeAsync(IProviderRuntime runtime, CancellationToken ct)
    {
        _runtime = runtime;
        // 端口可被用户在设置里改（providers.anytxt.options.endpoint）—— 官方文档说不建议改，
        // 但"不建议"不等于"不可能"，所以留这个口子。
        if (runtime.Settings.TryGetValue("endpoint", out var endpoint) && !string.IsNullOrWhiteSpace(endpoint))
            _runtime?.Log.Info(ProviderId, $"端点被设置覆盖：{endpoint}");
        return default;
    }

    // ───────────────────────── 健康与索引状态 ─────────────────────────

    public async ValueTask<ProviderHealth> ProbeHealthAsync(CancellationToken ct)
    {
        var call = await _client.CallAsync("anytxt.v1.status", null, ct).ConfigureAwait(false);

        if (!call.Ok)
        {
            var hint = call.ErrorCode == -32601
                ? "该端口不是 AnyTXT 的 SDK 接口 —— 确认它监听 9924"
                : "启动 AnyTXT Searcher，并确认许可证包含 SDK 功能（设置里可查）";
            return ProviderHealth.Down(ProviderId, call.Failure ?? "无法连接", hint);
        }

        var ready = call.Output.ValueKind == JsonValueKind.Object &&
                    call.Output.TryGetProperty("return", out var r) && r.ValueKind == JsonValueKind.True;

        return ready
            ? ProviderHealth.Ok(ProviderId, "索引已加载")
            : ProviderHealth.Down(ProviderId, "引擎尚未就绪（索引可能仍在构建）", "等索引建完再搜");
    }

    /// <summary>索引覆盖面随用户配置变化，声明空集合 = 未知（UI 不据此做限制）。</summary>
    public IReadOnlyCollection<string> IndexedExtensions => NoKnownExtensions;

    public async ValueTask<IndexState> GetIndexStateAsync(CancellationToken ct)
    {
        var call = await _client.CallAsync("anytxt.v1.status", null, ct).ConfigureAwait(false);
        if (!call.Ok) return IndexState.Unknown();

        var ready = call.Output.ValueKind == JsonValueKind.Object &&
                    call.Output.TryGetProperty("return", out var r) && r.ValueKind == JsonValueKind.True;
        // AnyTXT 只回答"好没好"，不报进度 —— 不编造数字。
        return ready ? IndexState.Complete(0) : new IndexState(false, 0, 0, "indexing");
    }

    // ───────────────────────── 调度契约 ─────────────────────────

    public bool EnabledForGlobalScope(SearchContext context) => true;

    /// <summary>
    /// 目录限定交给 <c>filterDir</c>。
    /// ⚠ 这里返回的目录<b>必须</b>是完整路径（含盘符）—— 传空串会被服务端强制成 <c>C:</c>。
    /// </summary>
    public string? TranslateScope(SearchContext context) =>
        context.IsDirectoryBounded ? context.RootPath : null;

    // ───────────────────────── 搜索 ─────────────────────────

    public async IAsyncEnumerable<SearchBatch> SearchAsync(
        SearchQuery query, SearchContext context,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var targets = ResolveTargets(context);
        if (targets.Count == 0)
        {
            yield return SearchBatch.Empty(ProviderId, query.RequestId);
            yield break;
        }

        var limit = Math.Clamp(query.ResultBudget, 1, AnytxtQueryTranslator.MaxLimit);
        var collected = new List<SearchResult>();
        var total = 0;
        var anySuccess = false;
        string? lastError = null;
        var notes = new List<string>();
        _driveBreakdown.Clear();

        // 串行：AnyTXT 是单进程索引，并发打它只会互相排队（同 EverythingProvider 的做法）
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var dir in targets)
            {
                ct.ThrowIfCancellationRequested();

                var translation = AnytxtQueryTranslator.Translate(query, dir, limit);
                foreach (var n in translation.Notes)
                    if (!notes.Contains(n)) notes.Add(n);

                if (translation.Request is not { } request) continue;

                var (rows, totalForDir, error) = await FetchAsync(request, ct).ConfigureAwait(false);
                if (error is not null) { lastError = error; continue; }

                // 逐盘记一行：全盘搜索是"多次查询后合并"，不记下来就没人看得出 D 盘到底问没问
                _runtime?.Log.Debug(ProviderId, $"盘 {dir} -> {rows.Count} 行（该盘总数 {totalForDir}）");
                _driveBreakdown.Add((dir, rows.Count));

                anySuccess = true;
                total += totalForDir;
                collected.AddRange(rows);
                if (collected.Count >= limit) break;
            }
        }
        finally
        {
            _gate.Release();
        }

        if (!anySuccess && lastError is not null)
        {
            _runtime?.Log.Warn(ProviderId, $"查询失败：{lastError}");
            yield return SearchBatch.Empty(ProviderId, query.RequestId);
            yield break;
        }

        if (notes.Count > 0)
            _runtime?.Log.Info(ProviderId, "降级说明：" + string.Join("；", notes));

        // 截到预算内（跨盘合并后可能超）
        var results = collected.Count > limit ? collected.GetRange(0, limit) : collected;

        yield return SearchBatch.Of(ProviderId, query.RequestId, results, total, isLast: true);
    }

    /// <summary>
    /// 本次要问哪些目录。
    /// <b>全盘搜索必须逐个盘符枚举</b> —— AnyTXT 的 <c>filterDir</c> 传空会被强制成 <c>C:</c>，
    /// 那样 D 盘的东西永远搜不到，而且用户看不出哪里不对。
    /// </summary>
    public static List<string> ResolveTargets(SearchContext context)
    {
        if (context.IsDirectoryBounded && context.RootPath is { Length: > 0 } root)
            return [root];

        return FixedDrives();
    }

    /// <summary>本机固定磁盘的根（<c>C:</c> / <c>D:</c> …）。</summary>
    public static List<string> FixedDrives()
    {
        var list = new List<string>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                if (d.DriveType != DriveType.Fixed) continue;
                var name = d.Name.TrimEnd('\\', '/');   // "C:\" → "C:"
                if (name.Length > 0) list.Add(name);
            }
        }
        catch
        {
            // 枚举失败就当没有盘 —— 上层会给出空结果而不是崩
        }
        return list;
    }

    /// <summary>取一个目录下的一页结果。返回（行、该目录总数、错误）。</summary>
    async Task<(List<SearchResult> Rows, int Total, string? Error)> FetchAsync(AnytxtRequest request, CancellationToken ct)
    {
        var call = await _client.CallAsync("anytxt.v1.getResult", request.ToParameters(), ct).ConfigureAwait(false);
        if (!call.Ok) return ([], 0, call.Failure);

        var rows = ParseRows(call.Output, request.FilterDir, out var pageCount);
        if (rows.Count == 0) return (rows, 0, null);

        // 本页装满 = 后面可能还有，这时才多花一次调用问总数；
        // 没装满说明这个目录已经到底了，总数就是行数（省掉一次往返）。
        var total = pageCount >= request.Limit
            ? await CountAsync(request, ct).ConfigureAwait(false)
            : rows.Count;

        return (rows, total, null);
    }

    async Task<int> CountAsync(AnytxtRequest request, CancellationToken ct)
    {
        var call = await _client.CallAsync("anytxt.v1.search", request.ToCountParameters(), ct).ConfigureAwait(false);
        if (!call.Ok || call.Output.ValueKind != JsonValueKind.Object) return 0;
        return call.Output.TryGetProperty("count", out var c) && c.TryGetInt32(out var n) ? n : 0;
    }

    /// <summary>
    /// 解析 <c>output.files</c>。
    /// <b>按 <c>output.field</c> 查名，不按下标硬编码</b> —— 字段表是服务端给的，
    /// 将来加字段时按下标读会静默读错列。
    /// </summary>
    internal static List<SearchResult> ParseRows(JsonElement output, string filterDir, out int pageCount)
    {
        pageCount = 0;
        var list = new List<SearchResult>();
        if (output.ValueKind != JsonValueKind.Object) return list;

        if (output.TryGetProperty("count", out var c) && c.TryGetInt32(out var n)) pageCount = n;
        if (!output.TryGetProperty("field", out var fieldEl) || fieldEl.ValueKind != JsonValueKind.Array) return list;
        if (!output.TryGetProperty("files", out var filesEl) || filesEl.ValueKind != JsonValueKind.Array) return list;

        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        foreach (var f in fieldEl.EnumerateArray())
        {
            var name = f.GetString();
            if (!string.IsNullOrEmpty(name)) index[name] = i;
            i++;
        }

        foreach (var row in filesEl.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array) continue;
            var mapped = MapRow(row, index, filterDir);
            if (mapped is not null) list.Add(mapped);
        }
        return list;
    }

    static SearchResult? MapRow(JsonElement row, Dictionary<string, int> index, string filterDir)
    {
        var path = Field(row, index, "file");
        if (string.IsNullOrEmpty(path)) return null;

        var fid = Field(row, index, "fid");
        var sizeText = Field(row, index, "size");
        var modifiedText = Field(row, index, "lastModify");

        long? size = long.TryParse(sizeText, out var sz) ? sz : null;
        DateTimeOffset? modified = long.TryParse(modifiedText, out var unix) && unix > 0
            ? DateTimeOffset.FromUnixTimeSeconds(unix)
            : null;

        var ext = Canonicalization.GetExtension(path);
        var name = Canonicalization.GetFileName(path);

        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(fid)) meta["fid"] = fid!;   // fid 是无符号 64 位数字串，别过数值类型
        meta["indexedBy"] = "anytxt";

        return new SearchResult
        {
            ProviderId = ProviderId,
            // fid 可能随重索引变化，所以融合键的锚点仍用路径；fid 只作为回查正文的凭据。
            ProviderItemId = path,
            Kind = KindOf(ext),
            Subtype = ext,
            // 这一条是 AnyTXT 的立身之本：命中的是**正文**
            Match = MatchKind.Content,
            Title = name,
            Subtitle = Path.GetDirectoryName(path) ?? filterDir,
            AutoCompleteText = name,
            Path = path,
            SizeBytes = size,
            ModifiedAt = modified,
            Metadata = meta,
            Icon = new IconHint { ShellIconPath = path, Badge = ext?.ToUpperInvariant() },
            Tags = [new ResultTag("正文", ResultTagTone.Info)],
            Payload = fid,
        };
    }

    static string? Field(JsonElement row, Dictionary<string, int> index, string name)
    {
        if (!index.TryGetValue(name, out var at)) return null;
        var n = 0;
        foreach (var v in row.EnumerateArray())
        {
            if (n++ != at) continue;
            return v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.ToString(),
                _ => null,
            };
        }
        return null;
    }

    /// <summary>
    /// 按扩展名给 <see cref="ResultKind"/>。
    /// <b>不猜分类</b>：结果进哪个 UI 分类由 Core 的 CategoryEngine 决定，Provider 只说"这是什么"。
    /// 自己先转小写而不是信任调用方 —— 这个函数是纯的、可能被任何地方调，
    /// 一个大小写差异就会让视频被当成普通文件。
    /// </summary>
    internal static ResultKind KindOf(string? ext) => ext?.TrimStart('.').ToLowerInvariant() switch
    {
        null or "" => ResultKind.File,
        "jpg" or "jpeg" or "png" or "gif" or "bmp" or "webp" or "tif" or "tiff" or "svg" or "heic" => ResultKind.Image,
        "mp4" or "mkv" or "avi" or "mov" or "wmv" or "flv" or "webm" or "m4v" => ResultKind.Video,
        "mp3" or "wav" or "flac" or "aac" or "ogg" or "m4a" or "wma" => ResultKind.Audio,
        "exe" or "msi" or "bat" or "cmd" or "ps1" or "lnk" => ResultKind.Application,
        _ => ResultKind.File,
    };

    // ───────────────────────── 跳到后端自己的界面 ─────────────────────────

    public string ExternalUiName => "AnyTXT";

    public bool CanOpenExternalUi => AnytxtLocator.FindExecutable() is not null;

    /// <summary>
    /// 把当前查询带到 AnyTXT 自己的窗口。
    /// <para>
    /// 命令行是**实测**出来的（不是照文档猜的）：<c>ATGUI.exe /s &lt;关键词&gt;</c> 会让已在运行的
    /// 实例把标题变成 <c>&lt;关键词&gt; - Anytxt Searcher …</c>，即真的执行了搜索，且复用同一进程。
    /// 完整开关见 <c>docs/research/ANYTXT-RPC-VERIFIED.md</c> §2。
    /// </para>
    /// </summary>
    public bool OpenExternalUi(string query)
    {
        var exe = AnytxtLocator.FindExecutable();
        if (exe is null)
        {
            _runtime?.Log.Warn(ProviderId, "找不到 ATGUI.exe，无法跳转到它的界面");
            return false;
        }

        var args = string.IsNullOrWhiteSpace(query)
            ? string.Empty
            : "/s \"" + query.Replace("\"", string.Empty) + "\"";

        var ok = _runtime?.Process.OpenFile(exe, args) ?? false;
        if (!ok) _runtime?.Log.Warn(ProviderId, $"唤起 AnyTXT 失败：{exe} {args}");
        return ok;
    }

    // ───────────────────────── 动作 ─────────────────────────

    public IReadOnlyList<ResultAction> GetActions(SearchResult result) => [];   // 通用动作由宿主统一注入

    public ValueTask<ActionResult> ExecuteAsync(ResultAction action, SearchResult result, CancellationToken ct)
        => ValueTask.FromResult(ActionResult.NotHandled());

    public ValueTask ShutdownAsync(CancellationToken ct)
    {
        _client.Dispose();
        _gate.Dispose();
        return default;
    }
}
