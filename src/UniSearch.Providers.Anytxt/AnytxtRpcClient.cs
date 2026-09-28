using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace UniSearch.Providers.Anytxt;

/// <summary>
/// 与 AnyTXT 的本地 JSON-RPC 对话。
/// <para>
/// <b>事实来源</b>：<c>docs/research/ANYTXT-RPC-VERIFIED.md</c> —— 动这里之前先读那份。
/// 两个最容易踩的点已经写死在下面：<b>端点是 9924 的 <c>/rpc</c></b>（不是 9920，那是内部
/// QJsonRpc 接口、参数形式也不同），以及 <b>params 直接给对象</b>（不是单元素数组）。
/// </para>
/// </summary>
public sealed class AnytxtRpcClient : IDisposable
{
    /// <summary>官方文档化的 JSON-RPC 端点。方法命名空间是 <c>anytxt.v1</c>，URL 里没有 <c>/v1</c> 段。</summary>
    public const string DefaultEndpoint = "http://127.0.0.1:9924/rpc";

    readonly HttpClient _http;
    readonly string _endpoint;
    long _id;

    public AnytxtRpcClient(string? endpoint = null, TimeSpan? timeout = null)
    {
        _endpoint = string.IsNullOrWhiteSpace(endpoint) ? DefaultEndpoint : endpoint!;
        // AnyTXT 说的是 HTTP/1.0；HttpClient 能处理，但超时得自己给足 —— 全盘首次查询会慢。
        _http = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(6) };
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public string Endpoint => _endpoint;

    /// <summary>
    /// 调一次方法。返回的 <see cref="AnytxtCall.Ok"/> 只表示"拿到了业务信封"，
    /// <b>不代表 errno 为 0</b> —— 实测 errno 是软状态（有结果时也可能是 1），不能当失败信号。
    /// </summary>
    public async Task<AnytxtCall> CallAsync(string method, object? parameters, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new RpcEnvelope
        {
            Id = Interlocked.Increment(ref _id),
            Method = method,
            Params = parameters ?? EmptyParams,
        });

        try
        {
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(_endpoint, content, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(body))
                return AnytxtCall.Failed($"HTTP {(int)resp.StatusCode}，响应为空（SDK 功能没启用？）");

            return Parse(body);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return AnytxtCall.Failed(ex.Message);
        }
    }

    internal static AnytxtCall Parse(string body)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            return AnytxtCall.Failed($"响应不是合法 JSON：{ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;

            // 协议/参数错误走顶层 error；业务状态走 errno
            if (root.TryGetProperty("error", out var err))
            {
                var code = err.TryGetProperty("code", out var c) && c.TryGetInt32(out var ci) ? ci : (int?)null;
                var msg = err.TryGetProperty("message", out var m) ? m.GetString() : null;
                return new AnytxtCall(false, 0, code, msg, default);
            }

            if (!root.TryGetProperty("result", out var result))
                return AnytxtCall.Failed("响应里既没有 result 也没有 error");

            var errno = result.TryGetProperty("errno", out var e) && e.TryGetInt32(out var ev) ? ev : 0;

            JsonElement output = default;
            if (result.TryGetProperty("data", out var data) &&
                data.TryGetProperty("output", out var o))
                output = o.Clone();

            return new AnytxtCall(true, errno, null, null, output);
        }
    }

    /// <summary>引擎/索引是否就绪（<c>anytxt.v1.status</c> → <c>output.return</c>）。</summary>
    public async Task<bool> IsReadyAsync(CancellationToken ct)
    {
        var call = await CallAsync("anytxt.v1.status", null, ct).ConfigureAwait(false);
        return call.Ok && call.Output.ValueKind == JsonValueKind.Object &&
               call.Output.TryGetProperty("return", out var r) && r.ValueKind == JsonValueKind.True;
    }

    public void Dispose() => _http.Dispose();

    static readonly Dictionary<string, object?> EmptyParams = [];

    /// <summary>手写而不是靠命名策略：JSON-RPC 的字段名是固定小写，跟 C# 命名无关。</summary>
    sealed record RpcEnvelope
    {
        [System.Text.Json.Serialization.JsonPropertyName("jsonrpc")] public string JsonRpc => "2.0";
        [System.Text.Json.Serialization.JsonPropertyName("id")] public long Id { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("method")] public required string Method { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("params")] public required object Params { get; init; }
    }
}

/// <summary>一次调用的结果。<see cref="Output"/> 是 <c>result.data.output</c> 的克隆。</summary>
public sealed record AnytxtCall(bool Ok, int Errno, int? ErrorCode, string? ErrorMessage, JsonElement Output)
{
    public static AnytxtCall Failed(string message) => new(false, 0, null, message, default);

    /// <summary>给人看的失败原因（成功时为 null）。</summary>
    public string? Failure => Ok ? null
        : ErrorCode is { } c ? $"[{c}] {ErrorMessage}"
        : ErrorMessage ?? "未知错误";
}
