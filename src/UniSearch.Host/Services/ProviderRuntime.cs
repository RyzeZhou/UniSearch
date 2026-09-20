using System.IO;
using UniSearch.Core.Usage;
using UniSearch.Host.Settings;
using UniSearch.Sdk.Runtime;

namespace UniSearch.Host.Services;

/// <summary>
/// 宿主注入给 Provider 的运行时服务（IProviderRuntime）。
/// <para>
/// <b>用法</b>：进程里只建一个（<c>Usage</c> 这类有状态的东西必须唯一），
/// 调 <see cref="ForProvider"/> 拿到绑定到某个 Provider 的视图再交给它。
/// <c>Settings</c> 是按 Provider 分的，所以不能拿同一个实例糊给所有 Provider ——
/// 那样它们会读到同一份配置，而 <c>IProviderRuntime</c> 的契约写的是"该 Provider 的配置节"。
/// </para>
/// </summary>
public sealed class ProviderRuntime : IProviderRuntime
{
    static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();

    /// <summary>进程内唯一的共享状态；<see cref="ForProvider"/> 出来的视图全部共用它。</summary>
    sealed class Shared
    {
        public required string DataDirectory { get; init; }
        public required IUsageStore Usage { get; init; }
        public required IUniSearchLog Log { get; init; }
        public required IProcessLauncher Process { get; init; }
    }

    readonly Shared _shared;
    readonly SettingsStore? _settings;
    readonly string? _providerId;

    public ProviderRuntime(IUniSearchLog log, IProcessLauncher process, SettingsStore? settings = null)
    {
        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UniSearch");
        Directory.CreateDirectory(dataDir);
        _shared = new Shared
        {
            DataDirectory = dataDir,
            Usage = new FileUsageStore(dataDir, TimeProvider.System),
            Log = log,
            Process = process,
        };
        _settings = settings;
    }

    ProviderRuntime(Shared shared, SettingsStore? settings, string providerId)
    {
        _shared = shared;
        _settings = settings;
        _providerId = providerId;
    }

    /// <summary>
    /// 绑定到某个 Provider 的视图：<c>Log</c>/<c>Process</c>/<c>Usage</c> 共享，
    /// 只有 <c>Settings</c> 换成它自己那一节。
    /// </summary>
    public IProviderRuntime ForProvider(string providerId) => new ProviderRuntime(_shared, _settings, providerId);

    public string DataDirectory => _shared.DataDirectory;
    public IUsageStore Usage => _shared.Usage;
    public IUniSearchLog Log => _shared.Log;
    public IProcessLauncher Process => _shared.Process;

    /// <summary>该 Provider 的配置节（<c>settings.json</c> 里 <c>providers.&lt;id&gt;.options</c>）。</summary>
    public IReadOnlyDictionary<string, string> Settings =>
        _settings is null || _providerId is null ? Empty : _settings.ProviderOptions(_providerId);

    public void RecordActivation(string usageKey, string? queryText, string providerId, string action)
        => _shared.Usage.Record(usageKey, queryText, providerId, action);

    public async ValueTask FlushAsync() => await _shared.Usage.FlushAsync(CancellationToken.None);
}
