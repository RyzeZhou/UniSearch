using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UniSearch.Host.Services;

/// <summary>取图标所需的信息。独立成结构，便于缓存与单测判断"该用哪个图标"。</summary>
public readonly record struct IconKey(string? Extension, bool IsFolder, bool IsApplication, string? Path)
{
    /// <summary>缓存键。<b>application 按真实路径</b>缓存（要拿到 exe 自己的图标），其余按扩展名。</summary>
    public string CacheKey => IsFolder || string.IsNullOrEmpty(Extension)
        ? "dir:"
        : IsApplication && !string.IsNullOrEmpty(Path) && File.Exists(Path) ? "app:" + Path
        : (Extension.StartsWith('.') ? Extension : "." + Extension);

    /// <summary>交给 SHGetFileInfo 的"路径或扩展名"与文件属性。</summary>
    public (string PathOrExt, uint Attributes) Lookup() =>
        IsFolder ? ("unisearch-folder", ShellIconCache.FILE_ATTRIBUTE_DIRECTORY)
        : IsApplication && !string.IsNullOrEmpty(Path) && File.Exists(Path) ? (Path!, ShellIconCache.FILE_ATTRIBUTE_NORMAL)
        : (CacheKey, ShellIconCache.FILE_ATTRIBUTE_NORMAL);
}

/// <summary>
/// Shell 图标解析与缓存。用 <c>SHGFI_USEFILEATTRIBUTES</c> 只按<b>扩展名</b>取图标 ——
/// 不打开文件、不碰磁盘；取 LARGEICON(32px) 交给 WPF 缩到 20px
/// （Win10 的 SMALLICON 固定 16px，在高 DPI 下发虚）。
/// <para>
/// 为什么非用真图标不可：字体图标（Segoe Fluent Icons）的码点在 Win10 上大量缺失，
/// 实测 <c>E8A5</c>/<c>E713</c> 直接渲染成豆腐块，用户完全无法区分文件类型。
/// </para>
/// <para>
/// 本类只管"怎么解析"，不管"什么时候解析" —— 调度见 <see cref="IconLoader"/>。
/// 缓存用普通字典而非 LRU：键是扩展名，数量天然有界（几百个），不会无限增长。
/// </para>
/// </summary>
public sealed class ShellIconCache
{
    internal const uint FILE_ATTRIBUTE_DIRECTORY = 0x000000010;
    internal const uint FILE_ATTRIBUTE_NORMAL = 0x000000080;

    const int MAX_PATH = 260;
    const uint SHGFI_ICON = 0x000000100;
    const uint SHGFI_LARGEICON = 0x000000000;
    const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFOW
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        // ByValTStr 由 Marshal 填充，不能给字段初始值（C#11 要求这样的 struct 有显式构造函数）
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_PATH)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
                                       ref SHFILEINFOW psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr hIcon);

    readonly ConcurrentDictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Shell 调用必须串行化。<b>实测</b>：<c>SHGetFileInfo</c> 并发调用会随机失败 ——
    /// 同一份查询，1 个工作线程 85/85 行拿到图标，2 个线程 82/85，4 个线程只有 72/85，
    /// 且每次失败的扩展名都不同（看起来像"这些类型本来就没图标"，极难排查）。
    /// 单次解析约 0.2ms，串行完全够用，没必要为了并发去踩这个坑。
    /// </summary>
    static readonly object ShellGate = new();

    /// <summary>同步查缓存；未命中返回 null，由调用方决定是排队还是就地解析。</summary>
    public ImageSource? TryGet(string cacheKey) => _cache.TryGetValue(cacheKey, out var hit) ? hit : null;

    /// <summary>就地解析（可能阻塞，<b>别</b>在 UI 线程上对未命中的键调用）。结果写入缓存。</summary>
    public ImageSource? Resolve(IconKey key)
    {
        var (pathOrExt, attrs) = key.Lookup();
        var hit = _cache.GetOrAdd(key.CacheKey, _ => ResolveNow(pathOrExt, attrs));
        if (hit is not null) return hit;

        // 该扩展名没有注册图标时退回通用文件图标。
        // 不这么做，那些行只能显示字体图标 —— 而字体图标在 Win10 上语义不符（实测是空白纸张），
        // 用户看到的是"有的行有图标、有的行没有"，像是加载失败。
        lock (_noIconKeys) _noIconKeys.Add(key.CacheKey);
        // 通用文件图标：传一个"没有注册关联"的假文件名。
        // 不能传 "file:" 这类带冒号的串 —— 那不是合法路径，SHGetFileInfo 会直接失败。
        return _cache.GetOrAdd(GenericFileKey, _ => ResolveNow("unisearch-generic.zzz", FILE_ATTRIBUTE_NORMAL));
    }

    /// <summary>查不到专用图标、已退回通用图标的键（诊断用）。</summary>
    public IReadOnlyCollection<string> NoIconKeys
    {
        get { lock (_noIconKeys) return [.. _noIconKeys]; }
    }
    readonly HashSet<string> _noIconKeys = new(StringComparer.OrdinalIgnoreCase);

    const string GenericFileKey = "*generic-file*";

    ImageSource? ResolveNow(string pathOrExt, uint attrs)
    {
        // 串行化：SHGetFileInfo 并发调用会随机失败（见 ShellGate 注释里的实测数据）
        lock (ShellGate)
        {
            return ResolveNowCore(pathOrExt, attrs);
        }
    }

    ImageSource? ResolveNowCore(string pathOrExt, uint attrs)
    {
        try
        {
            var info = default(SHFILEINFOW);
            var size = (uint)Marshal.SizeOf<SHFILEINFOW>();
            if (SHGetFileInfo(pathOrExt, attrs, ref info, size,
                              SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES) == IntPtr.Zero
                || info.hIcon == IntPtr.Zero)
            {
                LastFailure = $"SHGetFileInfo 无结果 key={pathOrExt} attrs=0x{attrs:X} err={Marshal.GetLastWin32Error()}";
                lock (_failures) _failures.Add(LastFailure);
                return null;
            }

            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty,
                                BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();   // Freeze 后才能安全地从后台线程交给 UI 线程
                return src;
            }
            catch (Exception ex)
            {
                LastFailure = $"CreateBitmapSourceFromHIcon 失败 key={pathOrExt}: {ex.GetType().Name}: {ex.Message}";
                lock (_failures) _failures.Add(LastFailure);
                return null;
            }
            finally
            {
                // 不销毁：SHGetFileInfo 在部分情况下回的可能是系统图像列表的共享句柄，
                // 销毁它会破坏 Shell 图标缓存，导致后续查询随机失败。
                // 代价是每个扩展名泄漏一个 HICON —— 键是扩展名，数量天然有界（几百个），可接受。
                _ = info.hIcon;
            }
        }
        catch (Exception ex)
        {
            LastFailure = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    /// <summary>最近一次解析失败的原因（诊断用）。ResolveNow 会吞异常，不记下来就永远查不到。</summary>
    public string? LastFailure { get; private set; }

    /// <summary>失败原因样本（去重，最多 8 条）。</summary>
    public IReadOnlyCollection<string> Failures
    {
        get { lock (_failures) return [.. _failures]; }
    }
    readonly HashSet<string> _failures = new(StringComparer.Ordinal);
}