namespace UniSearch.Sdk.Capabilities;

/// <summary>
/// long 底层的 [Flags] 枚举。用 <see cref="Has"/> 而不是 <c>HasFlag</c>：避免装箱，
/// 并且语义是“包含这些位”，不是 .NET Framework 时代的怪味道。
/// </summary>
public static class ProviderCapabilityExtensions
{
    public static bool Has(this ProviderCapability caps, ProviderCapability flag) => (caps & flag) == flag;

    public static bool HasAny(this ProviderCapability caps, ProviderCapability flags) => (caps & flags) != 0;

    /// <summary>能力位的可读列表，诊断面板与设置页用。</summary>
    public static IReadOnlyList<string> Describe(this ProviderCapability caps) =>
        Enum.GetValues<ProviderCapability>().Where(v => v != ProviderCapability.None && caps.Has(v))
            .Select(v => v.ToString()).ToList();
}
