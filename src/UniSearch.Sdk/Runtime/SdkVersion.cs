namespace UniSearch.Sdk.Runtime;

/// <summary>SDK 契约版本。加载器只接受 <see cref="SupportedMajor"/> 的 Provider。</summary>
public static class SdkVersion
{
    public const int SupportedMajor = 1;
    public const int Minor = 0;
    public static string Informational => $"{SupportedMajor}.{Minor}";
}
