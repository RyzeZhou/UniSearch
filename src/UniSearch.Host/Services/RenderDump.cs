using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UniSearch.Host.Services;

/// <summary>
/// 把窗口内容<b>离屏</b>渲染成 PNG。
/// <para>
/// 屏幕被锁 / 会话无显示设备时 <c>CopyFromScreen</c> 会报 "The handle is invalid"，
/// 但 <see cref="RenderTargetBitmap"/> 直接走视觉树，不依赖显示器 —— 所以这是
/// 验证「模板到底画没画出来」的可靠手段，也让 UI 回归可以被脚本断言。
/// </para>
/// <para>
/// <b>按显示器实际缩放渲染</b>：以前固定 96 DPI，拍出来是"100% 缩放下的样子"，
/// 而 175% 显示器上用户看到的字是 1.75 倍大的 —— 拿 96 DPI 的图去判断"字号大不大"
/// 必然与用户看到的对不上（2026-09-20 实测踩到）。现在用
/// <see cref="VisualTreeHelper.GetDpi"/> 拿真实缩放，图为物理像素尺寸。
/// </para>
/// </summary>
public static class RenderDump
{
    /// <summary>元素所在显示器上的缩放系数（175% → 1.75）。取不到时按 1.0。</summary>
    public static double PixelsPerDip(Visual v)
    {
        try
        {
            var s = VisualTreeHelper.GetDpi(v).PixelsPerDip;
            return s > 0 ? s : 1.0;
        }
        catch
        {
            return 1.0;
        }
    }

    public static void Run(Window w, string file, int? width = null, int? height = null, double? scale = null)
    {
        var pw = width ?? (int)w.ActualWidth;
        var ph = height ?? (int)w.ActualHeight;
        if (pw <= 0) pw = 760;
        if (ph <= 0) ph = 520;

        var s = scale ?? PixelsPerDip(w);
        var rtb = new RenderTargetBitmap(
            (int)Math.Ceiling(pw * s), (int)Math.Ceiling(ph * s), 96 * s, 96 * s, PixelFormats.Pbgra32);
        rtb.Render(w);
        rtb.Freeze();

        var dir = Path.GetDirectoryName(file);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(file);
        enc.Save(fs);
    }

    /// <summary>
    /// 渲染任意元素。用于弹出层：ContextMenu 在自己的 Popup 里（独立 HWND），
    /// 渲染主窗口根本拍不到它，只能单独渲染 Popup.Child。
    /// </summary>
    public static bool RunVisual(FrameworkElement el, string file, double? scale = null)
    {
        // 不能只信 ActualWidth/ActualHeight：弹出层刚重开时布局还没跑完，这两个值偏小，
        // 结果只渲染出菜单下半截。用后代视觉边界兜底，取两者较大值。
        var bounds = VisualTreeHelper.GetDescendantBounds(el);
        var w = (int)Math.Ceiling(Math.Max(el.ActualWidth, bounds.Right));
        var h = (int)Math.Ceiling(Math.Max(el.ActualHeight, bounds.Bottom));
        if (w <= 0 || h <= 0) return false;

        var s = scale ?? PixelsPerDip(el);
        var rtb = new RenderTargetBitmap(
            (int)Math.Ceiling(w * s), (int)Math.Ceiling(h * s), 96 * s, 96 * s, PixelFormats.Pbgra32);
        rtb.Render(el);
        rtb.Freeze();

        var dir = Path.GetDirectoryName(file);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(file);
        enc.Save(fs);
        return true;
    }
}