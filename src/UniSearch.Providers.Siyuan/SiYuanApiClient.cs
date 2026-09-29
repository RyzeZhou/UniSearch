using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace UniSearch.Providers.Siyuan;

/// <summary>
/// 与思源笔记<b>内核</b> HTTP API 对话（默认 <c>127.0.0.1:6806</c>）。
/// <para>
/// <b>事实来源</b>：<c>docs/research/SIYUAN-API-VERIFIED.md</c> —— 动这里之前先读那份。
/// 几个已经踩过的点写死在下面：
/// <list type="bullet">
/// <item><description><b>失败不是靠状态码判断的</b>：鉴权失败、SQL 被拒绝都返回
/// <b>HTTP 200</b>，真正的信号在响应体的 <c>code</c> 与 <c>data</c> 上；</description></item>
/// <item><description><b><c>code:0</c> + <c>data:null</c> 也是失败</b>：实测这是"这条 SQL 我不接受"
/// 的表示（例如带 <c>ESCAPE</c> 子句的语句），而 0 条结果是 <c>[]</c> / <c>[{"n":0}]</c>。
/// 把 <c>data:null</c> 当 0 条，会让"查询写错了"永远显示成"没搜到"；</description></item>
/// <item><description>非 127.0.0.1 访问要凭证，标头是 <c>Authorization: Token &lt;凭证&gt;</c>
/// （<c>Bearer</c> 也认，<c>X-Auth-Token</c> 不认）。</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class SiYuanApiClient : IDisposable
{
    public const string DefaultHost = "http://127.0.0.1";
    public const int DefaultPort = 6806;

    /// <summary>探活端点。<b>免鉴权</b>（实测跨机也免），所以健康探针在没配凭证时也能给出准确判断。</summary>
    public const string VersionPath = "/api/system/version";

    readonly HttpClient _http;
    readonly string _base;

    public SiYuanApiClient(string? host = null, int port = DefaultPort, string? token = null, TimeSpan? timeout = null)
    {
        var h = string.IsNullOrWhiteSpace(host) ? DefaultHost : host.Trim().TrimEnd('/');
        // 容忍用户把端口一起填进 host（"192.168.200.1:6806"）—— 拼成 ":6806:6806" 的报错
        // 从表面上看不出哪里错了（Zotero 那边踩过同型的坑：路径里多了个 /api）
        if (h.StartsWith("http://", StringComparison.OrdinalIgnoreCase) is false &&
            h.StartsWith("https://", StringComparison.OrdinalIgnoreCase) is false)
            h = "http://" + h;

        if (Uri.TryCreate(h, UriKind.Absolute, out var parsed) && !parsed.IsDefaultPort && parsed.Port > 0)
        {
            port = parsed.Port;
            h = $"{parsed.Scheme}://{parsed.Authority.Split(':')[0]}";
        }

        _base = $"{h}:{port}";
        Token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        _http = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(8) };
    }

    /// <summary>跨机访问的凭证（本机场景为 null）。<b>只存在内存与设置文件里，不入库、不写文档。</b></summary>
    public string? Token { get; }

    public string BaseUrl => _base;

    /// <summary>探活 + 版本锚定。<c>data</c> 是裸字符串（如 <c>"3.3.5"</c>）。</summary>
    public async Task<SiYuanProbe> ProbeAsync(CancellationToken ct)
    {
        var resp = await PostAsync(VersionPath, null, ct).ConfigureAwait(false);
        if (!resp.Ok) return new SiYuanProbe(false, null, resp.Error, resp.Hint);

        var version = resp.Data.ValueKind == JsonValueKind.String ? resp.Data.GetString() : null;
        return new SiYuanProbe(true, version, null, null);
    }

    /// <summary>笔记本清单（值域筛选器的候选值）。</summary>
    public async Task<SiYuanResponse> ListNotebooksAsync(CancellationToken ct)
        => await PostAsync("/api/notebook/lsNotebooks", null, ct).ConfigureAwait(false);

    /// <summary>跑一条 SQL。调用方保证语句已经过 <see cref="SiYuanQueryTranslator.Literal"/> 转义。</summary>
    public Task<SiYuanResponse> QuerySqlAsync(string statement, CancellationToken ct)
        => PostAsync("/api/query/sql", JsonSerializer.Serialize(new { stmt = statement }), ct);

    /// <summary>把一个块导出成 Markdown（带 frontmatter）。</summary>
    public Task<SiYuanResponse> ExportMdAsync(string id, CancellationToken ct)
        => PostAsync("/api/export/exportMdContent", JsonSerializer.Serialize(new { id }), ct);

    /// <summary>块 id → 人类可读路径。</summary>
    public Task<SiYuanResponse> HPathByIdAsync(string id, CancellationToken ct)
        => PostAsync("/api/filetree/getHPathByID", JsonSerializer.Serialize(new { id }), ct);

    public async Task<SiYuanResponse> PostAsync(string path, string? jsonBody, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, _base + path);
        req.Content = new StringContent(jsonBody ?? "{}", Encoding.UTF8, "application/json");
        if (Token is not null) req.Headers.TryAddWithoutValidation("Authorization", "Token " + Token);

        try
        {
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                return SiYuanResponse.Failed($"HTTP {(int)resp.StatusCode}",
                    (int)resp.StatusCode == 401 || (int)resp.StatusCode == 403
                        ? AuthHint : null);

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                // 兜底路由会返回 200 空 text/plain（实测 /mcp 就是这样）——
                // 说明请求打到了不存在的地方，而不是"查到了 0 条"
                var head = body.Length > 60 ? body[..60] + "…" : (body.Length == 0 ? "(空响应体)" : body);
                return SiYuanResponse.Failed($"响应不是 JSON：{head}");
            }

            using (doc)
            {
                var root = doc.RootElement;
                var code = root.TryGetProperty("code", out var c) && c.TryGetInt32(out var n) ? n : -999;
                var msg = root.TryGetProperty("msg", out var m) && m.ValueKind == JsonValueKind.String
                    ? m.GetString() : null;

                if (code != 0)
                    return SiYuanResponse.Failed($"思源返回 code={code}：{msg}", AuthHintFor(msg));

                if (!root.TryGetProperty("data", out var data) || data.ValueKind == JsonValueKind.Null)
                    return SiYuanResponse.Failed("思源拒绝了这条请求（code=0 但 data=null）",
                        "这类响应实测出现在 SQL 语法不被接受时（例如带 ESCAPE 子句）");

                return new SiYuanResponse(true, data.Clone(), null, null);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SiYuanResponse.Failed(ex.Message, "确认思源已启动，且「设置 → 关于」里的端口与凭证填对了");
        }
    }

    /// <summary>非环回访问的通用提示。用户看到"没有结果"时会去查笔记，而真正的问题是凭证。</summary>
    internal const string AuthHint =
        "宿主机思源对非 127.0.0.1 的访问要求访问授权码：在思源「设置 → 关于」里复制 API token 或访问授权码，" +
        "填进 providers.siyuan.options.token";

    internal static string? AuthHintFor(string? message)
    {
        if (message is null) return null;
        if (message.Contains("Access authorization code", StringComparison.OrdinalIgnoreCase)) return AuthHint;
        if (message.Contains("Auth failed [header: Authorization]", StringComparison.OrdinalIgnoreCase))
            return "凭证不对：检查 providers.siyuan.options.token 是否是思源当前的 API token / 访问授权码";
        return null;
    }

    public void Dispose() => _http.Dispose();
}

/// <param name="Data"><c>code=0</c> 时才有值；<c>null</c> 也算失败（见类注释）。</param>
public sealed record SiYuanResponse(bool Ok, JsonElement Data, string? Error, string? Hint)
{
    public static SiYuanResponse Failed(string error, string? hint = null) =>
        new(false, default, error, hint);
}

public sealed record SiYuanProbe(bool Available, string? Version, string? Error, string? Hint);
