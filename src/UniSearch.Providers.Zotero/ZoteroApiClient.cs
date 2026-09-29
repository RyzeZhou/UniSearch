using System.Net.Http;
using System.Text.Json;

namespace UniSearch.Providers.Zotero;

/// <summary>
/// 与 Zotero 桌面端<b>本地 API</b> 对话（<c>http://127.0.0.1:23119/api/</c>，Web API v3 的本地实现）。
/// <para>
/// <b>事实来源</b>：<c>docs/research/ZOTERO-LOCAL-API-VERIFIED.md</c> —— 动这里之前先读那份。
/// 几个已经踩过的点写死在下面：读请求不需要鉴权；<b>未知参数名会被静默忽略</b>（写错了不报错，
/// 只是没在筛）；本地 API <b>省略 limit 会一次返回全部</b>，所以 limit 必须显式给。
/// </para>
/// </summary>
public sealed class ZoteroApiClient : IDisposable
{
    /// <summary>Zotero 桌面端监听的根地址。</summary>
    public const string DefaultHost = "http://127.0.0.1:23119";

    /// <summary>本地 API 的前缀。<b>数据端点全在它下面</b>（<c>/api/users/0/items</c>）。</summary>
    public const string ApiPrefix = "/api";

    /// <summary>健康检查端点：返回 HTML <c>Zotero is running</c>。它在<b>根</b>上，不在 <c>/api</c> 下。</summary>
    public const string PingPath = "/connector/ping";

    readonly HttpClient _http;
    readonly string _host;
    readonly string _apiBase;

    public ZoteroApiClient(string? host = null, TimeSpan? timeout = null)
    {
        _host = (string.IsNullOrWhiteSpace(host) ? DefaultHost : host!).TrimEnd('/');

        // 用户可能把地址填成 http://127.0.0.1:23119/api —— 那就别再拼一次，
        // 否则所有请求都会 404，而 404 的原因从表面上看不出来。
        if (_host.EndsWith(ApiPrefix, StringComparison.OrdinalIgnoreCase))
        {
            _apiBase = _host;
            _host = _host[..^ApiPrefix.Length];
        }
        else
        {
            _apiBase = _host + ApiPrefix;
        }

        // Zotero 说的是 HTTP/1.0；首屏要给足时间，但也不能让它拖死整个查询。
        _http = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(6) };
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public string BaseUrl => _host;

    /// <summary>Zotero 在不在跑（不依赖 API 是否可用 —— 用户可能没开"允许其他应用通信"）。</summary>
    public async Task<bool> PingAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(_host + PingPath, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return false;
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return body.Contains("Zotero is running", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 探测本地 API 是否可用。
    /// <para>
    /// <b>只关心状态码与响应头，不解析响应体</b> —— <c>GET /api/</c> 返回的是纯文本
    /// <c>"Nothing to see here."</c>（Zotero 的玩笑），按 JSON 解析会当场炸掉，
    /// 于是"API 明明好好的"被误报成不可用。
    /// </para>
    /// </summary>
    public async Task<ZoteroProbe> ProbeApiAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(_apiBase + "/", ct).ConfigureAwait(false);

            if ((int)resp.StatusCode == 403)
                return new ZoteroProbe(false, 403, null, "本地 API 被拒绝（403）",
                    "在 Zotero 里打开 设置 → 高级 → 勾选「允许本机其他应用与 Zotero 通信」");

            if (!resp.IsSuccessStatusCode)
                return new ZoteroProbe(false, (int)resp.StatusCode, null, $"HTTP {(int)resp.StatusCode}", null);

            var version = resp.Headers.TryGetValues("X-Zotero-Version", out var v) ? string.Join("", v) : null;
            return new ZoteroProbe(true, 200, version, null, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ZoteroProbe(false, 0, null, ex.Message, null);
        }
    }

    /// <summary>
    /// 取一批对象。<paramref name="path"/> 形如 <c>/users/0/items</c>（user id 固定写 0 —— 本地 API 只服务本机登录用户）。
    /// <paramref name="query"/> 的值必须已经编码好。
    /// </summary>
    public async Task<ZoteroResponse> GetAsync(string path, IReadOnlyList<KeyValuePair<string, string>> query, CancellationToken ct)
    {
        var url = BuildUrl(path, query);

        try
        {
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            // ⚠ 403 = 用户没在「设置 → 高级」里允许其他应用与本机 Zotero 通信。
            // 这和"Zotero 没开"是两回事，提示必须分开，否则用户会一直去重启 Zotero。
            if ((int)resp.StatusCode == 403)
                return ZoteroResponse.Failed("本地 API 被拒绝（403）",
                    "在 Zotero 里打开 设置 → 高级 → 勾选「允许本机其他应用与 Zotero 通信」");

            if (!resp.IsSuccessStatusCode)
                return ZoteroResponse.Failed($"HTTP {(int)resp.StatusCode}");

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                // 数据端点本该只返回 JSON；返了别的说明请求打到了别的地方（路径写错、被别的东西占了端口）
                var head = body.Length > 60 ? body[..60] + "…" : body;
                return ZoteroResponse.Failed($"响应不是 JSON：{head}");
            }

            using (doc)
            {
                var total = TryReadTotal(resp);
                return new ZoteroResponse(true, doc.RootElement.Clone(), total, null, null);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ZoteroResponse.Failed(ex.Message);
        }
    }

    /// <summary>
    /// 取整个标签库（<c>GET /users/0/tags</c>）。
    /// <para>
    /// <b>limit=0 就是"全部"</b>（实测：返回 17 条 = 库里全部标签），所以不用翻页 ——
    /// 标签是给人勾选的短列表，为它实现 <c>Link</c> 头的翻页只会让 UI 变复杂。
    /// 万一将来标签上千，这里要改成按需搜索，而不是在 UI 上摊一千行。
    /// </para>
    /// <para>顺序<b>保持后端给的字母序</b>，不按条目数重排 —— 与 Zotero 自己的标签选择器一致，
    /// 用户在两处看到的是同一个顺序。</para>
    /// </summary>
    public async Task<ZoteroTagList> GetTagsAsync(CancellationToken ct)
    {
        var query = new List<KeyValuePair<string, string>>
        {
            new("limit", "0"),
            new("format", "json"),
        };

        var resp = await GetAsync("/users/0/tags", query, ct).ConfigureAwait(false);
        if (!resp.Ok) return ZoteroTagList.Failed(resp.Error ?? "读取标签失败", resp.Hint);

        var list = new List<ZoteroTag>();
        if (resp.Root.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in resp.Root.EnumerateArray())
            {
                if (t.ValueKind != JsonValueKind.Object) continue;
                if (!t.TryGetProperty("tag", out var nameEl) || nameEl.ValueKind != JsonValueKind.String) continue;
                var name = nameEl.GetString();
                if (string.IsNullOrWhiteSpace(name)) continue;

                long count = 0;
                if (t.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object &&
                    meta.TryGetProperty("numItems", out var n) && n.TryGetInt64(out var v))
                    count = v;

                list.Add(new ZoteroTag(name!, count));
            }
        }

        return new ZoteroTagList(true, list, null, null);
    }

    /// <summary>
    /// 拼 URL。<b>自己编码</b>而不是让 HttpClient 拼 Query：
    /// Zotero 的 <c>||</c>、<c>-</c>、空格都要按字面送过去，编码方式错一个字符语义就变了。
    /// </summary>
    internal static string BuildUrl(string baseUrl, string path, IReadOnlyList<KeyValuePair<string, string>> query)    {
        var sb = new System.Text.StringBuilder(baseUrl.TrimEnd('/')).Append(path);
        if (query.Count == 0) return sb.ToString();

        sb.Append('?');
        for (var i = 0; i < query.Count; i++)
        {
            if (i > 0) sb.Append('&');
            sb.Append(Uri.EscapeDataString(query[i].Key)).Append('=').Append(Uri.EscapeDataString(query[i].Value));
        }
        return sb.ToString();
    }

    string BuildUrl(string path, IReadOnlyList<KeyValuePair<string, string>> query) => BuildUrl(_apiBase, path, query);

    /// <summary>总数在响应头 <c>Total-Results</c> 里（本地 API 同样有）。</summary>
    static int? TryReadTotal(HttpResponseMessage resp)
    {
        if (!resp.Headers.TryGetValues("Total-Results", out var values)) return null;
        foreach (var v in values)
            if (int.TryParse(v, out var n)) return n;
        return null;
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>一次读取的结果。<see cref="Root"/> 是 JSON 根（数组或对象）。</summary>
public sealed record ZoteroResponse(bool Ok, JsonElement Root, int? Total, string? Error, string? Hint)
{
    public static ZoteroResponse Failed(string error, string? hint = null) =>
        new(false, default, null, error, hint);
}

/// <summary>本地 API 探测结果（不涉及响应体，只看状态码与响应头）。</summary>
public sealed record ZoteroProbe(bool Available, int StatusCode, string? Version, string? Error, string? Hint);

/// <param name="Count">该标签下的条目数（来自每条标签的 <c>meta.numItems</c>）。</param>
public sealed record ZoteroTag(string Name, long Count);

/// <summary>标签库读取结果。<see cref="Error"/> 非空时 <see cref="Tags"/> 必为空 ——
/// 调用方要能区分"没有标签"和"读不到标签"。</summary>
public sealed record ZoteroTagList(bool Ok, IReadOnlyList<ZoteroTag> Tags, string? Error, string? Hint)
{
    public static ZoteroTagList Failed(string error, string? hint = null) => new(false, [], error, hint);
}
