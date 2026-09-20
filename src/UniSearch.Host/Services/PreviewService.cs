using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UniSearch.Host.ViewModels;
using Vanara.PInvoke;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace UniSearch.Host.Services;

public enum PreviewKind { None, Folder, Image, Text, Thumbnail, Unavailable }

/// <summary>预览结果。UI 只按 <see cref="Kind"/> 选模板，不关心是怎么得到的。</summary>
public sealed record PreviewResult(
    PreviewKind Kind,
    ImageSource? Image,
    string? Text,
    string? Message,
    string? Detail = null)
{
    public static readonly PreviewResult None = new(PreviewKind.None, null, null, null);

    public bool HasImage => Image is not null;
    public bool HasText => !string.IsNullOrEmpty(Text);
    public bool HasMessage => !string.IsNullOrEmpty(Message);
    public bool HasDetail => !string.IsNullOrEmpty(Detail);
}

/// <summary>
/// 按类型提供预览内容。策略是"能用原生渲染就用原生，剩下的交给 Shell 缩略图"：
/// <list type="bullet">
/// <item>图片 → WPF 原生解码（能拿到真实像素尺寸，且不受缩略图缓存影响）</item>
/// <item>文本/代码 → 直接读前若干 KB（比缩略图有用得多）</item>
/// <item>其余（PDF、Office、视频…）→ <c>IShellItemImageFactory</c> 缩略图，
///       有多少算多少：注册了缩略图处理程序的类型自然就有预览，没有的如实说"没有可用预览"</item>
/// </list>
/// <para>
/// 不自研预览引擎（不做 PDF 渲染、不解析 Office 格式）：那是另一个量级的工程，
/// 而且系统里已经有现成的缩略图/预览处理程序。真需要更强的预览再考虑托管
/// <c>IPreviewHandler</c> 或接 QuickLook/Seer。
/// </para>
/// </summary>
public sealed class PreviewService
{
    /// <summary>Shell 缩略图也走串行化 —— 与图标同理，Shell 侧并发调用会随机失败。</summary>
    static readonly object ShellGate = new();

    const int MaxTextBytes = 48 * 1024;
    const int MaxTextChars = 6000;

    static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
    {
        "jpg", "jpeg", "jpe", "png", "gif", "bmp", "webp", "tif", "tiff", "ico", "heic", "avif", "svg",
    };

    /// <summary>只列"看了确实有用"的文本类。刻意保守，避免把二进制当文本糊一屏乱码。</summary>
    static readonly HashSet<string> TextExt = new(StringComparer.OrdinalIgnoreCase)
    {
        "txt", "md", "markdown", "log", "json", "jsonl", "xml", "yml", "yaml", "toml", "ini", "cfg", "conf",
        "csv", "tsv", "sql", "cs", "js", "mjs", "cjs", "ts", "tsx", "jsx", "py", "rb", "go", "rs", "java",
        "kt", "c", "h", "cpp", "hpp", "css", "scss", "html", "htm", "sh", "ps1", "bat", "cmd",
        "props", "targets", "xaml", "gradle", "gitignore", "editorconfig", "tex", "rst",
    };

    /// <summary>
    /// 诊断钩子：宿主接到日志上。预览链路为了"失败要降级而不是崩"会吞掉异常，
    /// 没有这条通道就查不出"为什么这个文件没有预览"。
    /// </summary>
    public static Action<string>? Trace { get; set; }

    public async Task<PreviewResult> LoadAsync(string? path, string? extension, bool isFolder, CancellationToken ct)
    {
        if (isFolder)
            return new PreviewResult(PreviewKind.Folder, null, null, "文件夹", path);

        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return new PreviewResult(PreviewKind.Unavailable, null, null, "文件不存在");

        var ext = (string.IsNullOrEmpty(extension) ? Path.GetExtension(path) : extension)
                  .TrimStart('.').ToLowerInvariant();

        Trace?.Invoke($"LoadAsync path={path} ext=[{ext}] isFolder={isFolder}");

        string detail;
        try
        {
            var fi = new FileInfo(path);
            detail = $"{Formatting.HumanSize(fi.Length)} · {fi.LastWriteTime:yyyy-MM-dd HH:mm}";
        }
        catch { detail = ""; }

        if (ImageExt.Contains(ext))
        {
            var img = await Task.Run(() => LoadImage(path), ct).ConfigureAwait(false);
            if (img is not null) return new PreviewResult(PreviewKind.Image, img, null, null, detail);
        }

        if (TextExt.Contains(ext))
        {
            var text = await Task.Run(() => ReadText(path), ct).ConfigureAwait(false);
            if (text is not null) return new PreviewResult(PreviewKind.Text, null, text, null, detail);
        }

        // PDF 单独走系统自带渲染器。实测本机没注册 PDF 缩略图处理器，
        // 只靠 IShellItemImageFactory 的话 PDF 永远显示"没有可用的预览"，
        // 而 PDF 恰恰是最需要预览的一类。
        if (ext == "pdf")
        {
            Trace?.Invoke($"命中 PDF 分支: {path}");
            var page = await Task.Run(() => LoadPdfFirstPage(path), ct).ConfigureAwait(false);
            Trace?.Invoke(page is null ? $"PDF 渲染失败: {LastError}" : "PDF 渲染成功");
            if (page is not null)
                return new PreviewResult(PreviewKind.Image, page, null, null, $"{detail} · 第 1 页");
        }

        var thumb = await Task.Run(() => LoadShellThumbnail(path), ct).ConfigureAwait(false);
        if (thumb is not null) return new PreviewResult(PreviewKind.Thumbnail, thumb, null, null, detail);

        return new PreviewResult(PreviewKind.Unavailable, null, null, "没有可用的预览", detail);
    }

    /// <summary>
    /// 渲染 PDF 第一页。用系统自带的 <c>Windows.Data.Pdf</c>（WinRT，Windows 10 1809+），
    /// 不引入任何第三方 PDF 库，也不依赖是否注册了 shell 缩略图处理器。
    /// 代价：宿主目标框架必须是带 Windows SDK 投影的 <c>net8.0-windows10.0.19041.0</c>。
    /// </summary>
    static ImageSource? LoadPdfFirstPage(string path)
    {
        try
        {
            var result = LoadPdfFirstPageAsync(path).GetAwaiter().GetResult();
            if (result is null) LastError = "PDF 渲染返回空（页数为 0？）";
            return result;
        }
        catch (Exception ex)
        {
            LastError = $"PDF 渲染失败: {ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    /// <summary>最近一次预览失败的原因。预览链路会吞异常（失败要降级而不是崩），不记下来就查不到。</summary>
    public static string? LastError { get; private set; }

    static async Task<ImageSource?> LoadPdfFirstPageAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path).AsTask().ConfigureAwait(false);
        var doc = await PdfDocument.LoadFromFileAsync(file).AsTask().ConfigureAwait(false);
        if (doc.PageCount == 0) return null;

        using var page = doc.GetPage(0);
        using var stream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(stream).AsTask().ConfigureAwait(false);

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = stream.AsStreamForRead();
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    /// <summary>按显示尺寸解码，不要原图全解 —— 一张 4000×3000 的图全解要 48MB，翻页会卡。</summary>
    static ImageSource? LoadImage(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;   // 立刻读完，别占着文件句柄
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.DecodePixelWidth = 720;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>读文本预览。二进制检测（出现 NUL 字节就当二进制）比按扩展名猜可靠。</summary>
    static string? ReadText(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var len = (int)Math.Min(fs.Length, MaxTextBytes);
            if (len == 0) return "(空文件)";

            var buf = new byte[len];
            var read = fs.Read(buf, 0, len);
            for (var i = 0; i < read; i++)
                if (buf[i] == 0) return null;      // NUL → 二进制，交给缩略图

            var text = System.Text.Encoding.UTF8.GetString(buf, 0, read);
            var truncated = fs.Length > read;
            if (text.Length > MaxTextChars)
            {
                text = text[..MaxTextChars];
                truncated = true;
            }
            return truncated ? text + "\n\n…（已截断）" : text;
        }
        catch { return null; }
    }

    /// <summary>
    /// Shell 缩略图。用 THUMBNAILONLY：拿不到真缩略图就返回失败，
    /// 而不是退回一个图标 —— 预览窗格里放图标没有意义（列表里已经有图标了）。
    /// </summary>
    static ImageSource? LoadShellThumbnail(string path)
    {
        lock (ShellGate)
        {
            try
            {
                var factory = Shell32.SHCreateItemFromParsingName<Shell32.IShellItemImageFactory>(path, null);
                factory.GetImage(new SIZE(512, 512),
                    Shell32.SIIGBF.SIIGBF_THUMBNAILONLY | Shell32.SIIGBF.SIIGBF_BIGGERSIZEOK | Shell32.SIIGBF.SIIGBF_RESIZETOFIT,
                    out var hbm);

                using (hbm)
                {
                    if (hbm is null || hbm.IsInvalid) return null;
                    var src = Imaging.CreateBitmapSourceFromHBitmap(
                        hbm.DangerousGetHandle(), nint.Zero, Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    return src;
                }
            }
            catch { return null; }
        }
    }
}
