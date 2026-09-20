namespace UniSearch.Sdk.Contracts;

public enum HealthState { Ready, Degraded, Unavailable, Unknown }

/// <summary>Provider 健康状态。UI 的底部状态条与设置页直接展示它。</summary>
public sealed record ProviderHealth
{
    public required string ProviderId { get; init; }
    public required HealthState State { get; init; }
    public string? Detail { get; init; }
    /// <summary>可直接执行的动作提示，例如“启动 AnyTXT 服务”。</summary>
    public string? Hint { get; init; }
    public string? Version { get; init; }

    public static ProviderHealth Ok(string id, string? version = null) =>
        new() { ProviderId = id, State = HealthState.Ready, Version = version };

    public static ProviderHealth Down(string id, string detail, string? hint = null) =>
        new() { ProviderId = id, State = HealthState.Unavailable, Detail = detail, Hint = hint };

    public static ProviderHealth Degraded(string id, string detail, string? hint = null) =>
        new() { ProviderId = id, State = HealthState.Degraded, Detail = detail, Hint = hint };
}
