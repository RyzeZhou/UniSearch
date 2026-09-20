using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using UniSearch.Sdk.Runtime;

namespace UniSearch.Host.Settings;

/// <summary>
/// 设置文件的读写与内存副本。<b>进程内唯一</b>，设置窗口、热键、Broker、Provider 都从这里取。
/// <para>
/// 读盘策略：文件不存在 → 用默认值（首次运行是常态，不是错误）；文件损坏 → <b>备份成
/// <c>settings.bad-&lt;时间戳&gt;.json</c> 再用默认值</b>，绝不静默覆盖 —— 用户的配置可能只是
/// 手改时多了个逗号，直接盖掉等于把他的设置弄丢了。
/// </para>
/// <para>
/// 写盘策略：先写 <c>.tmp</c> 再原子替换。直接覆写时断电/崩溃会留下半个 JSON，
/// 下次启动就变成"设置全没了"。
/// </para>
/// </summary>
public sealed class SettingsStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        // 默认编码器会把 `+`、`<`、`>` 以及所有非 ASCII 转义成 \uXXXX ——
        // 于是热键写成 "Alt\u002BWindows\u002BSpace"、中文路径变成一串 \u5E26。
        // 这文件是给人看、给人改的，必须用宽松转义。
        // （Unsafe 这个名字是针对"嵌进 HTML"的场景；这里是本地配置文件，不存在那个风险。）
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    readonly IUniSearchLog? _log;

    public SettingsStore(string dataDirectory, IUniSearchLog? log = null)
    {
        _log = log;
        Directory.CreateDirectory(dataDirectory);
        FilePath = Path.Combine(dataDirectory, "settings.json");
        Current = Load();
    }

    public string FilePath { get; }

    /// <summary>当前生效的设置。<b>只读用途</b>；要改就走 <see cref="Save"/>，好让变更事件发出去。</summary>
    public UniSearchSettings Current { get; private set; }

    /// <summary>设置被保存后触发（参数是新的设置对象）。订阅方负责把值应用到自己的部件上。</summary>
    public event Action<UniSearchSettings>? Changed;

    /// <summary>把内存里的当前设置序列化成 JSON（<see cref="UniSearchSettings.Clone"/> 也用它）。</summary>
    internal static string Serialize(UniSearchSettings s) => JsonSerializer.Serialize(s, JsonOptions);

    /// <summary>某个 Provider 的配置节（<c>IProviderRuntime.Settings</c> 的数据源）。</summary>
    public IReadOnlyDictionary<string, string> ProviderOptions(string providerId)
        => Current.ForProvider(providerId).Options;

    public bool IsProviderEnabled(string providerId) => Current.ForProvider(providerId).Enabled;

    UniSearchSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                _log?.Info("settings", $"没有设置文件，使用默认值（会在首次保存时创建 {FilePath}）");
                var fresh = new UniSearchSettings();
                fresh.Normalize();
                return fresh;
            }

            var json = File.ReadAllText(FilePath);
            var loaded = JsonSerializer.Deserialize<UniSearchSettings>(json, JsonOptions);
            if (loaded is null)
            {
                QuarantineFile("反序列化得到 null");
                var fresh = new UniSearchSettings();
                fresh.Normalize();
                return fresh;
            }

            loaded.Normalize();
            _log?.Info("settings", $"已从 {FilePath} 载入设置");
            return loaded;
        }
        catch (Exception ex)
        {
            // 坏文件一定要留证据：用户手改坏了 JSON 时，直接覆盖会让他连"我改了什么"都看不到
            QuarantineFile($"{ex.GetType().Name}: {ex.Message}");
            var fresh = new UniSearchSettings();
            fresh.Normalize();
            return fresh;
        }
    }

    void QuarantineFile(string reason)
    {
        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var bad = Path.Combine(Path.GetDirectoryName(FilePath)!, $"settings.bad-{stamp}.json");
            File.Move(FilePath, bad, overwrite: true);
            _log?.Warn("settings", $"设置文件无法解析（{reason}），已备份到 {bad} 并改用默认值");
        }
        catch (Exception ex)
        {
            _log?.Warn("settings", "备份损坏的设置文件失败", ex);
        }
    }

    /// <summary>
    /// 保存并通知。返回是否成功 —— 失败要能让设置窗口如实报错，而不是假装保存成功。
    /// </summary>
    /// <param name="notify">
    /// false = <b>只落盘、不触发 <see cref="Changed"/></b>。给纯 UI 状态用（列宽、列顺序、排序键）：
    /// 它们也属于"用户的设置"，该保存，但拖一下列宽就重跑一次查询显然不对。
    /// </param>
    public bool Save(UniSearchSettings settings, bool notify = true)
    {
        settings.Normalize();

        var tmp = FilePath + ".tmp";
        try
        {
            File.WriteAllText(tmp, Serialize(settings));
            File.Move(tmp, FilePath, overwrite: true);   // 同卷替换，不会留下半个文件
        }
        catch (Exception ex)
        {
            _log?.Error("settings", "保存设置失败", ex);
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 清不掉也无所谓 */ }
            return false;
        }

        Current = settings;
        _log?.Info("settings", notify ? $"设置已保存到 {FilePath}" : $"界面布局已保存到 {FilePath}（不重跑查询）");
        if (!notify) return true;

        try { Changed?.Invoke(settings); }
        catch (Exception ex) { _log?.Error("settings", "应用设置时抛异常", ex); }
        return true;
    }
}
