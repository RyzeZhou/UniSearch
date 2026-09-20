namespace UniSearch.Sdk.Capabilities;

/// <summary>Provider 的静态自述。加载、诊断、设置页、能力匹配都读它。</summary>
public sealed record ProviderDescriptor
{
    /// <summary>全局唯一、小写、稳定。出现在配置与结果溯源里，禁止改动。<c>"everything"</c> / <c>"anytxt"</c> / <c>"zotero"</c>。</summary>
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>点分版本。<see cref="UniSearchProviderAttribute.ApiVersion"/> 与之独立。</summary>
    public string Version { get; init; } = "0.1.0";

    public string? Description { get; init; }

    /// <summary>能力集合。<b>必须</b>与实现的接口一致，加载器会做交叉校验。</summary>
    public required ProviderCapability Capabilities { get; init; }

    /// <summary>调度权重，同分类内竞争展示位时用。Everything=100，全文检索类=60，慢的在线类=10。</summary>
    public int Priority { get; init; } = 50;

    /// <summary>建议首屏延迟预算。Broker 据此决定先出骨架还是等结果。</summary>
    public TimeSpan LatencyHint { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>该 Provider 的启用条件：例如需要外部进程在跑。</summary>
    public ExternalDependency? DependsOn { get; init; }
}

/// <summary>外部依赖声明，用于设置页给出“装什么 / 开什么”的可操作提示。</summary>
public sealed record ExternalDependency
{
    public required string Name { get; init; }
    /// <summary>探测方式：可执行文件名、Windows 服务名、HTTP 端点或注册表键。</summary>
    public DependencyProbeKind ProbeKind { get; init; }
    public string ProbeValue { get; init; } = "";
    public string? InstallUrl { get; init; }
    /// <summary>缺失时是否致命（Everything 是；Zotero 不是）。</summary>
    public bool Required { get; init; }
}

public enum DependencyProbeKind { None, Process, ExecutableOnPath, RegistryKey, HttpEndpoint, NamedPipe, WindowClass }
