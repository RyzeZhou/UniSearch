namespace UniSearch.Sdk.Contracts;

using UniSearch.Sdk.Capabilities;
using UniSearch.Sdk.Model;

/// <summary>
/// 所有搜索后端的<b>唯一必备</b>接口。除此之外的一切能力都是可选接口，
/// Broker 用 <c>is</c> 探测，因此新增能力永远不需要改动已有 Provider。
/// </summary>
/// <remarks>
/// 实现约定：
/// <list type="bullet">
/// <item><description>必须可被多线程调用（同一实例可并发处理多个 RequestId）。</description></item>
/// <item><description>必须尊重 <paramref name="ct"/>；被取消后不得再 yield。</description></item>
/// <item><description>不得抛异常穿越边界之外的地方 —— 抛出的异常会被 Broker 捕获并转成 ProviderFault，UI 上显示为该分类的降级提示。</description></item>
/// <item><description>不得自己做去重/分类/排序，只如实返回语义字段。</description></item>
/// </list>
/// </remarks>
public interface ISearchProvider
{
    ProviderDescriptor Descriptor { get; }

    /// <summary>
    /// 执行一次搜索。<b>要求</b>：尽快 yield 首批（哪怕 20 条），不要在流水线里预取全部再返回。
    /// </summary>
    IAsyncEnumerable<SearchBatch> SearchAsync(SearchQuery query, SearchContext context, CancellationToken ct);

    /// <summary>可选：连接外部服务、预热索引。失败不应阻止加载，只应让 <see cref="ProbeHealthAsync"/> 报故障。</summary>
    ValueTask InitializeAsync(Runtime.IProviderRuntime runtime, CancellationToken ct) => default;

    ValueTask ShutdownAsync(CancellationToken ct) => default;

    /// <summary>健康探测（Everything 是否在运行、AnyTXT 服务端口是否可达）。默认认为健康。</summary>
    ValueTask<ProviderHealth> ProbeHealthAsync(CancellationToken ct) =>
        ValueTask.FromResult(ProviderHealth.Ok(Descriptor.Id));
}
