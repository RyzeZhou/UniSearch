namespace UniSearch.Sdk.Runtime;

/// <summary>
/// 宿主注入给 Provider 的服务集合。Provider <b>不得</b>引用 UniSearch.Core / App，
/// 只能通过这里取能力 —— 这是插件与宿主之间唯一的耦合面。
/// </summary>
public interface IProviderRuntime
{
    /// <summary><c>%LOCALAPPDATA%\UniSearch</c>。Provider 把自己的缓存写在自己 id 的子目录下。</summary>
    string DataDirectory { get; }

    /// <summary>该 Provider 的配置节（来自 settings.json 的 <c>providers.&lt;id&gt;</c>）。</summary>
    IReadOnlyDictionary<string, string> Settings { get; }

    IUsageStore Usage { get; }

    IUniSearchLog Log { get; }

    /// <summary>宿主提供的启动器（ShellExecute 语义，含“以管理员运行”）。Provider 的动作执行走这里。</summary>
    IProcessLauncher Process { get; }

    /// <summary>记一次用户激活（打开/运行/选中）。参与后续排序。</summary>
    void RecordActivation(string usageKey, string? queryText, string providerId, string action);
}

/// <summary>
/// 用户激活历史。
/// <para>
/// <b>双键设计来自 Flow Launcher</b>（docs/research/REF-2 §2.3）：
/// 同一查询下点过同一条结果的权重是“全局点过该结果”的 5 倍 ——
/// 否则“我搜 chrome 时点过一个 pdf”会永久污染所有查询的排序。
/// </para>
/// </summary>
public interface IUsageStore
{
    /// <summary>全局维度的衰减权重。</summary>
    double GetWeight(string usageKey);

    /// <summary>含查询维度的组合权重（约当 <c>5×queryScoped + global</c>）。<paramref name="queryText"/> 为空时退化为全局。</summary>
    double GetWeight(string usageKey, string? queryText);

    int GetActivationCount(string usageKey);

    DateTimeOffset? LastActivatedAt(string usageKey);

    /// <summary>最常用的键（用于空查询时的“常用”分区）。<paramref name="kindPrefix"/> 可为 null。</summary>
    IReadOnlyList<string> GetTopKeys(string? kindPrefix, int take);

    void Record(string usageKey, string? queryText, string providerId, string action);

    /// <summary>把内存里的计数落盘（宿主在空闲/退出时调用）。</summary>
    Task FlushAsync(CancellationToken ct);
}

/// <summary>由宿主实现的启动器，Provider 用它执行“打开/运行”类动作。</summary>
public interface IProcessLauncher
{
    bool OpenFile(string path, string? arguments = null, string? workingDirectory = null);
    bool OpenFolder(string path, bool selectFile = false);
    bool StartUri(string uri);
    bool RunVerb(string path, string verb);
    /// <summary>用户取消 UAC 应返回 false 且<b>不算错误</b>（EverythingToolbar 的处理方式）。</summary>
    bool RunAsAdmin(string path, string? arguments = null);
    /// <summary>在控制台/终端里打开所在目录（CmdPal 的 OpenInConsoleCommand 语义）。</summary>
    bool OpenInConsole(string path);
    /// <summary>
    /// 把文本放进系统剪贴板（Provider 的"复制 xx"类动作走这里）。
    /// 默认 false：宿主没实现时动作要如实报失败，不能假装复制成功了。
    /// </summary>
    bool CopyToClipboard(string text) => false;
}

/// <summary>极简日志门面。SDK 不绑定第三方日志库；宿主可把它桥接到任何 sink。</summary>
public interface IUniSearchLog
{
    void Debug(string providerId, string message);
    void Info(string providerId, string message);
    void Warn(string providerId, string message, Exception? error = null);
    void Error(string providerId, string message, Exception? error = null);
}
