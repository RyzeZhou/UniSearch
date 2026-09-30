using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UniSearch.Sdk.Capabilities;
using UniSearch.Sdk.Runtime;
using Vanara.PInvoke;

namespace UniSearch.Host.Services;

/// <summary>
/// 左栏后端图标的解析（B4）。按 <see cref="ProviderIcon"/> 的<b>线索</b>而非写死路径找软件：
/// <list type="number">
/// <item><see cref="ProviderIcon.KnownPath"/> —— Provider 用自家探测（含"从正在运行进程反查"）
/// 预先拿到的入口，最优先；</item>
/// <item>开始菜单快捷方式 —— 便携 / 绿色安装只要建过 .lnk 就能抽到真图标；</item>
/// <item>App Paths 注册表 —— 正规安装器的普遍痕迹；</item>
/// <item>全 miss → 调用方退到 <see cref="ProviderIcon.FallbackGlyph"/> 字形。</item>
/// </list>
/// <para>
/// 图标抽取用 <c>SIIGBF_ICONONLY</c>：exe 与 .lnk（自动解引用目标）都返回干净图标，
/// 不带快捷方式小箭头。Shell 调用照例串行化（并发随机失败，见 ShellIconCache 的实测记录）。
/// </para>
/// </summary>
public static class ProviderIconResolver
{
    static readonly object ShellGate = new();

    /// <summary>按 providerId 缓存。RefreshSources 会重建 SourceViewModel，别每次都重扫开始菜单。</summary>
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Result?> Cache =
        new(StringComparer.Ordinal);

    public sealed record Result(ImageSource Icon, string SourcePath);

    /// <summary>返回 null = 没找到软件或抽不出图标，调用方走字形兜底。</summary>
    public static Result? Resolve(string providerId, ProviderIcon icon)
        => Cache.GetOrAdd(providerId, _ => ResolveCore(icon));

    static Result? ResolveCore(ProviderIcon icon)
    {
        var source =
            (icon.KnownPath is { Length: > 0 } k && File.Exists(k) ? k : null)
            ?? ShellAppLocator.FindStartMenuShortcut(icon.ShellKeyword)
            ?? ShellAppLocator.FindViaAppPaths(icon.ExecutableName);
        if (source is null) return null;

        var image = ExtractIcon(source);
        return image is null ? null : new Result(image, source);
    }

    static ImageSource? ExtractIcon(string path)
    {
        lock (ShellGate)
        {
            try
            {
                var factory = Shell32.SHCreateItemFromParsingName<Shell32.IShellItemImageFactory>(path, null);
                factory.GetImage(new SIZE(64, 64),
                    Shell32.SIIGBF.SIIGBF_ICONONLY | Shell32.SIIGBF.SIIGBF_BIGGERSIZEOK | Shell32.SIIGBF.SIIGBF_RESIZETOFIT,
                    out var hbm);
                using (hbm)
                {
                    if (hbm is null || hbm.IsInvalid) return null;
                    var src = Imaging.CreateBitmapSourceFromHBitmap(
                        hbm.DangerousGetHandle(), nint.Zero, Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();   // 后台解析、UI 使用
                    return src;
                }
            }
            catch { return null; }
        }
    }
}
