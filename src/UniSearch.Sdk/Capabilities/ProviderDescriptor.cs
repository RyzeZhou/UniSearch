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

    /// <summary>
    /// 左栏来源的图标声明（B4）。null = 所有后端共用一个通用放大镜字形。
    /// <para>
    /// <b>声明的是"怎么认出这个软件"，不是一条写死的路径</b> —— 宿主按
    /// <see cref="ProviderIcon"/> 里的关键字去开始菜单 / App Paths 里找，
    /// 便携安装（没有注册表、路径任意）只要建过快捷方式就能抽到真图标；
    /// 全都找不到时用 <see cref="ProviderIcon.FallbackGlyph"/> 兜底。
    /// </para>
    /// </summary>
    public ProviderIcon? Icon { get; init; }
}

/// <summary>声明"如何在系统里认出一个外部软件"。字段都是线索而非路径 —— 探测由宿主做。</summary>
/// <param name="ShellKeyword">在开始菜单里认出它的关键字（"Anytxt"）—— .lnk 文件名含它即命中。</param>
/// <param name="ExecutableName">App Paths 注册表键名（"zotero.exe"）—— 安装器普遍会写。</param>
/// <param name="FallbackGlyph">系统里根本找不到时的 Segoe Fluent 字形兜底（紧凑态也要能分辨后端）。</param>
/// <param name="KnownPath">
/// Provider 用自家探测（含"从正在运行的进程反查"这种宿主做不到的招）预先拿到的入口路径，
/// 可为 null。非 null 且文件存在时<b>最优先</b>使用。
/// </param>
public sealed record ProviderIcon(string ShellKeyword, string ExecutableName, string FallbackGlyph, string? KnownPath = null);

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
