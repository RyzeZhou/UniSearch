namespace UniSearch.Sdk.Runtime;

/// <summary>
/// 标记一个程序集入口类型。宿主扫描 plugins 目录里的 dll 时以它为凭据。
/// 一个程序集可以有多个（示例插件常把若干小后端打包在一起）。
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class UniSearchProviderAttribute : Attribute
{
    public UniSearchProviderAttribute(string id, string displayName)
    {
        Id = id;
        DisplayName = displayName;
    }

    /// <summary>稳定小写 id，用于配置键与结果溯源。</summary>
    public string Id { get; }
    public string DisplayName { get; }

    /// <summary>本 Provider 遵循的 SDK 契约版本。宿主 <c>SdkVersion.SupportedMajor</c> 不匹配则拒绝加载并明确报错，而不是运行到一半 NullReference。</summary>
    public int ApiVersion { get; set; } = 1;

    /// <summary>为 false 时默认不启用（例如需要用户显式安装 AnyTXT 后才打开）。</summary>
    public bool EnabledByDefault { get; set; } = true;

    /// <summary>依赖的外部后端，用于设置页给出“未检测到”提示。</summary>
    public string? RequiredApp { get; set; }
    public string? InstallHint { get; set; }
}
