using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using UniSearch.Sdk.Model;

namespace UniSearch.Core.Filters;

/// <summary>
/// 一个结果筛选器（"文件"、"图片"、"生信相关"…）。
/// <para>
/// <b>为什么要有定义文件</b>：筛选器集合是会长的东西 —— 今天要"生信相关"（pdb/cif/fasta…），
/// 明天可能要"3D 打印"（stl/obj/3mf）、"设计稿"（fig/sketch/psd）。把这些写死在代码里，
/// 每加一组就得改代码重发版；写成 JSON 定义文件，用户自己就能加。
/// </para>
/// <para>
/// 匹配条件是<b>并集</b>：扩展名命中、或类型命中、或文件名正则命中，都算这条结果属于该筛选器。
/// </para>
/// </summary>
public sealed record FilterDefinition
{
    /// <summary>稳定 id（小写、连字符）。会落进配置与日志，改名等于换一个筛选器。</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>Segoe Fluent Icons 码点（如 <c>E9D9</c>）；不给就用通用图标。</summary>
    public string? Glyph { get; init; }

    /// <summary>说明文字（设置窗口/工具提示用）。</summary>
    public string? Description { get; init; }

    /// <summary>
    /// 只在这些后端下出现（后端 id，如 <c>everything</c>）。<b>空 = 所有后端都出现</b>。
    /// 这就是"不同的搜索后端应用不同的筛选器"的落点：全文检索后端不会有"文件夹"这种筛选器，
    /// 而文件索引后端不会有"正文命中 N 处"这种。
    /// </summary>
    public IReadOnlyList<string> Providers { get; init; } = [];

    /// <summary>标签栏里的次序（内置分类占 0–900，自定义默认 500）。</summary>
    public int Order { get; init; } = 500;

    /// <summary>扩展名（小写、不含点）。</summary>
    public IReadOnlyList<string> Extensions { get; init; } = [];

    /// <summary>结果类型名（<see cref="ResultKind"/> 的名字，如 <c>Folder</c>、<c>Image</c>）。</summary>
    public IReadOnlyList<string> Kinds { get; init; } = [];

    /// <summary>文件名正则（可选）。</summary>
    public string? NamePattern { get; init; }

    /// <summary>为 true 时只作为别名存在，不在标签栏里显示。</summary>
    public bool Hidden { get; init; }

    /// <summary>编译好的正则（<see cref="NamePattern"/> 为空则 null）。</summary>
    [JsonIgnore]
    public Regex? NameRegex { get; init; }

    /// <summary>没有任何匹配条件 = 会匹配一切，属于配置写错，加载时会被剔除。</summary>
    [JsonIgnore]
    public bool IsEmpty => Extensions.Count == 0 && Kinds.Count == 0 && NameRegex is null;

    /// <summary>这个筛选器在某个后端下是否可见。</summary>
    public bool AppliesTo(string? providerId) =>
        Providers.Count == 0 || (providerId is { Length: > 0 } id &&
                                 Providers.Contains(id, StringComparer.OrdinalIgnoreCase));

    /// <summary>一条结果是否属于这个筛选器（并集语义）。</summary>
    public bool Matches(SearchResult r)
    {
        if (Extensions.Count > 0 && r.Extension is { Length: > 0 } ext &&
            Extensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            return true;

        if (Kinds.Count > 0 && Kinds.Contains(r.Kind.ToString(), StringComparer.OrdinalIgnoreCase))
            return true;

        if (NameRegex is not null && NameRegex.IsMatch(r.Title))
            return true;

        return false;
    }
}

/// <summary>filters.json 的形状。多来源合并，后者覆盖同 id 的前者。</summary>
public sealed class FilterFile
{
    public int Version { get; set; } = 1;

    /// <summary>写文件的人看的注释（JSON 没有注释，就用一个字段承载）。</summary>
    public string? Comment { get; set; }

    public List<FilterDefinition> Filters { get; set; } = [];
}

/// <summary>
/// 筛选器目录：把若干 <c>filters.json</c>（程序自带一份模板 + 用户自己一份）合并成一张表。
/// <para>
/// <b>为什么是 JSON 而不是 YAML</b>：项目已经用 <c>System.Text.Json</c> 读设置（零新增依赖、
/// 宽松转义让中文与反斜杠可读），再引一个 YAML 解析库只为了省几个引号不划算。
/// 这里还开了"允许注释、允许尾随逗号"，手改体验接近 YAML。
/// </para>
/// </summary>
public sealed class FilterCatalog
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    FilterCatalog(IReadOnlyList<FilterDefinition> all, IReadOnlyList<string> sources, IReadOnlyList<string> problems)
    {
        All = all;
        Sources = sources;
        Problems = problems;
    }

    public IReadOnlyList<FilterDefinition> All { get; }

    /// <summary>实际读到的文件（诊断用：用户改完不生效时，先看他改的是不是这一份）。</summary>
    public IReadOnlyList<string> Sources { get; }

    /// <summary>加载过程中的问题（文件坏了、id 非法、正则编译失败…）。不静默吞掉。</summary>
    public IReadOnlyList<string> Problems { get; }

    public static FilterCatalog Empty { get; } = new([], [], []);

    /// <summary>
    /// 按顺序读入若干文件并合并：<b>后面的覆盖前面的同 id 项</b>，
    /// 所以调用方把"程序自带模板"放前面、"用户自己的"放后面。
    /// 文件不存在不算错误（第一次运行没有用户文件是常态）。
    /// </summary>
    public static FilterCatalog Load(params string?[] jsonPaths)
    {
        var merged = new Dictionary<string, FilterDefinition>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        var sources = new List<string>();
        var problems = new List<string>();

        foreach (var path in jsonPaths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
            sources.Add(path);

            FilterFile? file;
            try
            {
                file = JsonSerializer.Deserialize<FilterFile>(File.ReadAllText(path), JsonOptions);
            }
            catch (Exception ex)
            {
                problems.Add($"{Path.GetFileName(path)} 解析失败：{ex.Message}");
                continue;
            }

            if (file?.Filters is null) continue;

            foreach (var raw in file.Filters)
            {
                var def = Normalize(raw, path, problems);
                if (def is null) continue;

                if (merged.ContainsKey(def.Id) && !order.Contains(def.Id, StringComparer.OrdinalIgnoreCase))
                    order.Add(def.Id);
                else if (!merged.ContainsKey(def.Id))
                    order.Add(def.Id);

                merged[def.Id] = def;
            }
        }

        var list = order.Where(merged.ContainsKey).Select(id => merged[id])
                        .OrderBy(f => f.Order).ThenBy(f => f.Name, StringComparer.CurrentCulture)
                        .ToList();
        return new FilterCatalog(list, sources, problems);
    }

    /// <summary>校验并规范化一条定义。返回 null 表示这条不可用（原因记进 problems）。</summary>
    static FilterDefinition? Normalize(FilterDefinition raw, string source, List<string> problems)
    {
        var where = Path.GetFileName(source);

        var id = (raw.Id ?? string.Empty).Trim().ToLowerInvariant();
        if (id.Length == 0)
        {
            problems.Add($"{where}：有一条筛选器没有 id，已跳过");
            return null;
        }
        // id 会进配置和日志，只留安全字符（"生信 相关" 这种写法自动变成 "生信-相关"）
        id = new string(id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());

        var name = (raw.Name ?? string.Empty).Trim();
        if (name.Length == 0) name = id;

        var extensions = CleanExtensions(raw.Extensions);
        var kinds = CleanKinds(raw.Kinds, id, where, problems);

        Regex? regex = null;
        if (!string.IsNullOrWhiteSpace(raw.NamePattern))
        {
            try
            {
                regex = new Regex(raw.NamePattern, RegexOptions.IgnoreCase | RegexOptions.Compiled,
                                  TimeSpan.FromMilliseconds(200));
            }
            catch (Exception ex)
            {
                problems.Add($"{where}：筛选器「{name}」的 namePattern 不是合法正则（{ex.Message}），已忽略该条件");
            }
        }

        var def = raw with
        {
            Id = id,
            Name = name,
            Extensions = extensions,
            Kinds = kinds,
            NameRegex = regex,
            Glyph = string.IsNullOrWhiteSpace(raw.Glyph) ? null : raw.Glyph.Trim(),
        };

        // 只在"这条定义本身没问题、却一个条件都不剩"时才报空 ——
        // 否则写错一个类型名会同时收到"类型不认识"和"没有匹配条件"两条，
        // 后者是前者的后果，重复报只会让人以为有两处错误。
        if (def.IsEmpty && !problems.Any(p => p.Contains($"「{name}」") || p.Contains($"「{id}」")))
        {
            problems.Add($"{where}：筛选器「{name}」没有任何匹配条件（extensions / kinds / namePattern 全空），已跳过");
            return null;
        }
        return def.IsEmpty ? null : def;
    }

    /// <summary>扩展名统一成"小写、不含点、去重、去空"。用户写 <c>.PDB</c> 或 <c>pdb</c> 都该能用。</summary>
    static IReadOnlyList<string> CleanExtensions(IReadOnlyList<string>? raw)
    {
        if (raw is null) return [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var item in raw)
        {
            var e = (item ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant();
            if (e.Length == 0 || !seen.Add(e)) continue;
            list.Add(e);
        }
        return list;
    }

    /// <summary>类型名按 <see cref="ResultKind"/> 解析；不认识的名字要报出来（而不是静默丢弃）。</summary>
    static IReadOnlyList<string> CleanKinds(IReadOnlyList<string>? raw, string id, string where, List<string> problems)
    {
        if (raw is null) return [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var item in raw)
        {
            var k = (item ?? string.Empty).Trim();
            if (k.Length == 0 || !seen.Add(k)) continue;
            if (!Enum.TryParse<ResultKind>(k, ignoreCase: true, out var parsed))
            {
                problems.Add($"{where}：筛选器「{id}」里的类型「{k}」不认识（可用值：{string.Join("/", Enum.GetNames<ResultKind>())}）");
                continue;
            }
            list.Add(parsed.ToString());
        }
        return list;
    }

    /// <summary>
    /// 某组后端下该显示的筛选器（不含 hidden）。
    /// <para>
    /// 传的是<b>当前生效的后端集合</b>（可能是多元素，见"默认来源集合"），
    /// 空/null 表示不限定 —— 那时给全量，因为 UI 还没有来源信息可用。
    /// </para>
    /// </summary>
    public IReadOnlyList<FilterDefinition> For(IReadOnlyList<string>? providerIds)
    {
        if (providerIds is not { Count: > 0 })
            return All.Where(f => !f.Hidden).ToList();

        return All.Where(f => !f.Hidden &&
                              (f.Providers.Count == 0 ||
                               f.Providers.Any(p => providerIds.Contains(p, StringComparer.OrdinalIgnoreCase))))
                  .ToList();
    }

    public FilterDefinition? Find(string? id) =>
        id is { Length: > 0 } ? All.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase)) : null;
}
