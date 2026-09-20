using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Threading;
using UniSearch.Host.ViewModels;

namespace UniSearch.Host.Services;

/// <summary>
/// 图标加载调度：<b>命中缓存就地返回，未命中丢后台线程，且按缓存键合并同类请求</b>，
/// 解析完批量刷回 UI 线程。
/// <para>
/// <b>为什么用专用 STA 线程 + CoInitializeEx，而不是 Task.Run 的线程池：</b>
/// <c>SHGetFileInfo</c> 是 Shell API，要求调用线程初始化过 COM。线程池线程是 MTA 且
/// 未初始化 COM，实测会导致部分扩展名（如 <c>.mjs</c>/<c>.jsonl</c>）查不到图标而返回失败 ——
/// 看起来像"这些类型本来就没图标"，其实是调用方式不对。
/// </para>
/// <para>
/// 架构源自 EverythingToolbar 的 <c>IconLoader</c>（MIT），但有一处<b>不能照搬</b>：
/// ET 的列表是增量 diff 的，我们是<b>每次快照整表重建</b>，同一批扩展名会被反复请求
/// （实测一次搜索两次快照 = 150 个请求），照搬"有界积压 + 超出即丢弃"会让 85 行里只有 5 行
/// 拿到真图标。所以这里按缓存键（扩展名）合并：85 行通常只有十几个不同扩展名。
/// </para>
/// </summary>
public sealed class IconLoader : IDisposable
{
    readonly ShellIconCache _cache;
    readonly Dispatcher _dispatcher;
    readonly CancellationTokenSource _cts = new();

    readonly object _gate = new();
    /// <summary>缓存键 → 正在等这个图标的行（同一个 .pdf 会有很多行在等）。</summary>
    readonly Dictionary<string, List<WeakReference<ResultItemViewModel>>> _waiting = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>已在队列/解析中的键，避免重复排队。</summary>
    readonly HashSet<string> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    readonly List<(WeakReference<ResultItemViewModel> Row, ImageSource Icon)> _done = [];

    readonly BlockingCollection<Job> _queue = new();
    bool _flushScheduled;
    readonly int _maxInFlight;
    readonly Thread[] _workers;

    readonly record struct Job(string CacheKey, IconKey Key);

    // 诊断计数（诊断面板/自检用）
    public int CacheHits;
    public int Queued;
    public int Coalesced;
    public int Resolved;
    public int Dropped;
    public int Failed;
    volatile string? _lastError;
    public string? LastError => _lastError;

    /// <summary>查不到专用图标、已退回通用文件图标的键（诊断）。</summary>
    public IReadOnlyCollection<string> NoIconKeys => _cache.NoIconKeys;

    /// <summary>图标解析失败的样本原因（诊断）。</summary>
    public IReadOnlyCollection<string> IconFailures => _cache.Failures;

    public IconLoader(ShellIconCache cache, Dispatcher dispatcher, int workers = 2, int maxInFlight = 256)
    {
        _cache = cache;
        _dispatcher = dispatcher;
        _maxInFlight = maxInFlight;

        _workers = new Thread[workers];
        for (var i = 0; i < workers; i++)
        {
            var t = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = $"UniSearch.IconWorker{i}",
            };
            t.SetApartmentState(ApartmentState.STA);   // Shell API 要 STA
            t.Start();
            _workers[i] = t;
        }
    }

    /// <summary>
    /// 为一行的图标做安排。<b>必须在 UI 线程调用</b>（缓存命中时会直接写 <c>row.Icon</c>）。
    /// </summary>
    public void Load(ResultItemViewModel row, IconKey key)
    {
        var cacheKey = key.CacheKey;

        if (_cache.TryGet(cacheKey) is { } hit)
        {
            row.Icon = hit;            // 命中：零延迟。稳态下绝大多数行走这条
            CacheHits++;
            return;
        }

        var weak = new WeakReference<ResultItemViewModel>(row);
        lock (_gate)
        {
            if (_inFlight.Contains(cacheKey))
            {
                _waiting[cacheKey].Add(weak);   // 同键已在解析，搭便车
                Coalesced++;
                return;
            }
            if (_inFlight.Count >= _maxInFlight) { Dropped++; return; }

            _inFlight.Add(cacheKey);
            _waiting[cacheKey] = [weak];
        }

        Queued++;
        try { _queue.Add(new Job(cacheKey, key)); }
        catch (InvalidOperationException)   // 已 CompleteAdding（正在退出）
        {
            lock (_gate) { _inFlight.Remove(cacheKey); _waiting.Remove(cacheKey); }
        }
    }

    void WorkerLoop()
    {
        CoInitializeEx(nint.Zero, COINIT_APARTMENTTHREADED);
        try
        {
            foreach (var job in _queue.GetConsumingEnumerable(_cts.Token))
            {
                // 单个任务失败绝不能让工作线程退出 —— 线程一死队列就永久停滞
                // （实测过：未捕获异常带走线程后，计数会冻在"排队 25 / 完成 17"不动，看起来像卡死）
                ImageSource? icon = null;
                try
                {
                    icon = _cache.Resolve(job.Key);   // 阻塞点在这里，不在 UI 线程
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref Failed);
                    _lastError = ex.Message;
                }

                lock (_gate)
                {
                    if (_waiting.Remove(job.CacheKey, out var rows) && icon is not null)
                        foreach (var w in rows) _done.Add((w, icon));
                    _inFlight.Remove(job.CacheKey);
                }

                if (icon is not null)
                {
                    Interlocked.Increment(ref Resolved);
                    ScheduleFlush();
                }
            }
        }
        catch (OperationCanceledException) { /* 正常退出 */ }
        catch (Exception ex)
        {
            Interlocked.Increment(ref Failed);
            _lastError = ex.Message;
        }
        finally
        {
            CoUninitialize();
        }
    }

    /// <summary>把这一批合并成一次 Dispatcher 调用，避免每个图标各排一次队。</summary>
    void ScheduleFlush()
    {
        lock (_gate)
        {
            if (_flushScheduled) return;
            _flushScheduled = true;
        }

        _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            List<(WeakReference<ResultItemViewModel> Row, ImageSource Icon)> batch;
            lock (_gate)
            {
                batch = [.. _done];
                _done.Clear();
                _flushScheduled = false;
            }

            foreach (var (weak, icon) in batch)
                if (weak.TryGetTarget(out var row))
                    row.Icon = icon;    // 回到 UI 线程写，绑定才会刷新
        });
    }

    public void Dispose()
    {
        _cts.Cancel();
        _queue.CompleteAdding();
        foreach (var t in _workers)
            try { t.Join(TimeSpan.FromSeconds(2)); } catch { /* 退出时不纠缠 */ }
        _queue.Dispose();
        _cts.Dispose();
    }

    // ── Shell API 要求调用线程初始化 COM ────────────────
    const uint COINIT_APARTMENTTHREADED = 0x2;

    [DllImport("ole32.dll")]
    static extern int CoInitializeEx(nint pvReserved, uint dwCoInit);

    [DllImport("ole32.dll")]
    static extern void CoUninitialize();
}
