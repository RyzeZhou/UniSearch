namespace UniSearch.Sdk.Contracts;

using UniSearch.Sdk.Model;

/// <summary>
/// Provider 流式返回的一批结果。用 <see cref="IAsyncEnumerable{T}"/> 而非一次性 List：
/// Everything 可以 5ms 出首批，Zotero 可能 400ms 才出，UI 需要先到先显示。
/// </summary>
public sealed record SearchBatch
{
    public required string ProviderId { get; init; }
    public required long RequestId { get; init; }
    public required IReadOnlyList<SearchResult> Results { get; init; }

    /// <summary>该 Provider 声明的总命中数（用于“还有 N 条”）。null = 未知。</summary>
    public int? TotalAvailable { get; init; }

    /// <summary>为 true 表示该 Provider 已不再产出，Broker 可提前收敛。</summary>
    public bool IsLast { get; init; }

    public static SearchBatch Empty(string providerId, long requestId, bool isLast = true) =>
        new() { ProviderId = providerId, RequestId = requestId, Results = [], IsLast = isLast };

    public static SearchBatch Of(string providerId, long requestId, IReadOnlyList<SearchResult> results, int? total = null, bool isLast = false) =>
        new() { ProviderId = providerId, RequestId = requestId, Results = results, TotalAvailable = total, IsLast = isLast };
}
