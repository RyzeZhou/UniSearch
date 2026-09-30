using System.IO;
using UniSearch.Sdk.Runtime;

namespace UniSearch.Host.Services;

/// <summary>
/// filters.json 的文件监视（热重载的"耳朵"）。
/// <para>
/// 监视每个配置文件的<b>所在目录</b>而不是文件本身 —— 编辑器保存常见的"写临时文件再改名替换"
/// 触发的是 Renamed，盯文件会漏；Changed / Created / Renamed 都要接。
/// 一串事件防抖成一次回调（VS Code 保存一次会连发多个 Changed），回调在线程池上触发，
/// 落回 UI 线程是调用方的事（SetFilterCatalog 会碰 ObservableCollection 并重跑查询）。
/// </para>
/// </summary>
public sealed class FilterFileWatcher : IDisposable
{
    readonly List<FileSystemWatcher> _watchers = [];
    readonly System.Timers.Timer _debounce;
    readonly Action _onReload;
    readonly IUniSearchLog _log;

    public FilterFileWatcher(IEnumerable<string?> files, Action onReload, IUniSearchLog log)
    {
        _onReload = onReload;
        _log = log;
        _debounce = new System.Timers.Timer(400) { AutoReset = false };
        _debounce.Elapsed += (_, _) =>
        {
            try { _onReload(); }
            catch (Exception ex) { _log.Error("filters", "热重载回调失败", ex); }
        };

        foreach (var dir in files.Where(f => !string.IsNullOrWhiteSpace(f))
                                 .Select(f => Path.GetDirectoryName(f))
                                 .Where(d => !string.IsNullOrWhiteSpace(d))
                                 .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var watcher = new FileSystemWatcher(dir!, "filters.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };
            watcher.Changed += (_, _) => _debounce.Start();
            watcher.Created += (_, _) => _debounce.Start();
            watcher.Renamed += (_, _) => _debounce.Start();
            // 删除也算改动（用户整个删掉用户文件 = 回到只用程序自带定义）。
            // 编辑器"删旧再改名换上"的保存序列在这里天然安全：几个事件先后到，
            // 防抖计时器每次重置，最终只按尘埃落定后的磁盘状态重载一次。
            watcher.Deleted += (_, _) => _debounce.Start();
            watcher.Error += (_, e) => _log.Warn("filters",
                $"文件监视出错：{e.GetException().Message}（改动保存后仍不生效就重启程序）");
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
    }

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
        _debounce.Dispose();
    }
}
