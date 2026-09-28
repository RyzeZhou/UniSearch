namespace UniSearch.Core.Filters;

/// <summary>
/// 一个<b>筛选器模板</b>：标签栏的"组织方式"。
/// <para>
/// <b>为什么要有模板</b>：筛选器定义回答的是"这条结果属于哪一桶"，与后端无关；但"我现在想看哪几桶"
/// 是随场景变的 —— 找数据文件时要生信/3D/配置那几组，查文献时要条目类型/标签，找代码时又是另一套。
/// 定义一多，全平铺在标签栏里就是一长排。模板 = 一组定义的有序引用，可整体切换、可按后端给默认值。
/// </para>
/// <para>
/// <b>只引用不复制</b>：<see cref="Filters"/> 里放的是 <see cref="FilterDefinition.Id"/>。
/// 所以同一个定义可以进多个模板，改一次扩展名所有模板一起生效；模板层也<b>不</b>定义
/// 多标签组合的交/并语义（那归筛选器组合那件事，模板只是容器）。
/// </para>
/// </summary>
public sealed record FilterTemplate
{
    /// <summary>稳定 id（小写、连字符）。会落进配置与日志，改名等于换一个模板。</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>模板下拉里的次序。</summary>
    public int Order { get; init; } = 500;

    /// <summary>
    /// 引用的筛选器定义 id，<b>顺序即标签栏次序</b>（模板内以这里的先后为准，不再按定义的 order 排）。
    /// 可以为空数组（"极简"模板就是一个标签都不要）。
    /// </summary>
    public IReadOnlyList<string> Filters { get; init; } = [];

    /// <summary>
    /// 这个模板是哪些后端的默认。<c>"*"</c> = 全局兜底默认（全局视图、以及没有专属默认的后端都走它）。
    /// 留空 = 不主动认领任何后端，只能被用户手动选中或钉住。
    /// </summary>
    public IReadOnlyList<string> DefaultFor { get; init; } = [];

    /// <summary>
    /// 只在哪些后端下<b>可选</b>（后端 id，如 <c>zotero</c>）。<b>留空 = 所有后端可选</b>。
    /// 注意它管的是"下拉里出不出得来"，不改变 <see cref="DefaultFor"/> 的认领。
    /// </summary>
    public IReadOnlyList<string> Providers { get; init; } = [];

    /// <summary>这个模板在某个后端下是否可选（<c>providers</c> 留空、或还不知道来源时，都算可选）。</summary>
    public bool AvailableFor(string? providerId) =>
        Providers.Count == 0 ||
        providerId is not { Length: > 0 } ||
        Providers.Contains(providerId, StringComparer.OrdinalIgnoreCase);
}
