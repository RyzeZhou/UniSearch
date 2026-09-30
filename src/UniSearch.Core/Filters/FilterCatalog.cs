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

    /// <summary>
    /// 语义<b>子</b>类型（<see cref="SearchResult.Subtype"/>，小写连字符：<c>journal-article</c>、
    /// <c>preprint</c>、<c>attachment</c>、<c>pdf</c>…）。
    /// <para>
    /// 为什么单开一维：<see cref="Kinds"/> 只有"文献条目"这一档粗粒度，而 Zotero 有 40 种条目类型 ——
    /// 想表达"期刊论文"就够不着。子类型正是后端如实声明的那一层，拿它当筛选维度最直接。
    /// </para>
    /// </summary>
    public IReadOnlyList<string> Subtypes { get; init; } = [];

    /// <summary>文件名正则（可选）。</summary>
    public string? NamePattern { get; init; }

    /// <summary>为 true 时只作为别名存在，不在标签栏里显示。</summary>
    public bool Hidden { get; init; }

    /// <summary>编译好的正则（<see cref="NamePattern"/> 为空则 null）。</summary>
    [JsonIgnore]
    public Regex? NameRegex { get; init; }

    /// <summary>没有任何匹配条件 = 会匹配一切，属于配置写错，加载时会被剔除。</summary>
    [JsonIgnore]
    public bool IsEmpty => Extensions.Count == 0 && Kinds.Count == 0 && Subtypes.Count == 0 && NameRegex is null;

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

        if (Subtypes.Count > 0 && r.Subtype is { Length: > 0 } sub &&
            Subtypes.Contains(sub, StringComparer.OrdinalIgnoreCase))
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

    /// <summary>
    /// 模板节（v2）。<b>null = 这个文件根本没写 templates</b>，这与"写了空数组"不是一回事：
    /// 没写 = 保持现状平铺（升级第一天行为不变），写了 = 标签栏的组织方式交给模板。
    /// </summary>
    public List<FilterTemplate>? Templates { get; set; }
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

    FilterCatalog(IReadOnlyList<FilterDefinition> all, IReadOnlyList<FilterTemplate> templates,
                  IReadOnlyList<string> sources, IReadOnlyList<string> problems)
    {
        All = all;
        Templates = templates;
        Sources = sources;
        Problems = problems;
    }

    public IReadOnlyList<FilterDefinition> All { get; }

    /// <summary>
    /// 按 <see cref="FilterTemplate.Order"/> 排好序的模板；<b>空 = 没有任何文件写过 templates 节</b>，
    /// 那时标签栏走现状平铺（见 <see cref="ResolveTemplate"/>）。
    /// </summary>
    public IReadOnlyList<FilterTemplate> Templates { get; }

    /// <summary>实际读到的文件（诊断用：用户改完不生效时，先看他改的是不是这一份）。</summary>
    public IReadOnlyList<string> Sources { get; }

    /// <summary>加载过程中的问题（文件坏了、id 非法、正则编译失败…）。不静默吞掉。</summary>
    public IReadOnlyList<string> Problems { get; }

    public static FilterCatalog Empty { get; } = new([], [], [], []);

    /// <summary>
    /// 按顺序读入若干文件并合并：<b>后面的覆盖前面的同 id 项</b>，
    /// 所以调用方把"程序自带模板"放前面、"用户自己的"放后面。
    /// 文件不存在不算错误（第一次运行没有用户文件是常态）。
    /// <para>
    /// <c>filters</c> 与 <c>templates</c> <b>各自独立合并</b>：用户文件里只写 filters 时，
    /// 程序模板里的 templates 照样生效（不会被"用户没写"抹掉）。
    /// </para>
    /// </summary>
    public static FilterCatalog Load(params string?[] jsonPaths)
    {
        var merged = new Dictionary<string, FilterDefinition>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        var mergedTemplates = new Dictionary<string, (FilterTemplate Template, string Source)>(StringComparer.OrdinalIgnoreCase);
        var sources = new List<string>();
        var problems = new List<string>();

        foreach (var path in jsonPaths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;

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

            // 成功解析才算"读到的来源"—— Sources 的语义是"实际生效的文件"，
            // 坏文件从 Problems 里看（热重载判定也据此区分"坏文件"与"没配置"）
            sources.Add(path);

            if (file is null) continue;

            foreach (var raw in file.Filters ?? [])
            {
                var def = Normalize(raw, path, problems);
                if (def is null) continue;

                if (merged.ContainsKey(def.Id) && !order.Contains(def.Id, StringComparer.OrdinalIgnoreCase))
                    order.Add(def.Id);
                else if (!merged.ContainsKey(def.Id))
                    order.Add(def.Id);

                merged[def.Id] = def;
            }

            foreach (var raw in file.Templates ?? [])
            {
                var tpl = NormalizeTemplate(raw, path, problems);
                if (tpl is not null) mergedTemplates[tpl.Id] = (tpl, path);
            }
        }

        var list = order.Where(merged.ContainsKey).Select(id => merged[id])
                        .OrderBy(f => f.Order).ThenBy(f => f.Name, StringComparer.CurrentCulture)
                        .ToList();

        // 模板的引用校验与默认冲突都得等"所有文件合并完"才判 —— 程序模板引用的定义可能只在
        // 用户那份文件里（或反过来），边读边校验会把合法引用误判成"不存在"。
        // 排序在这里一次定死：后面 DefaultTemplate 取 FirstOrDefault 就是"order 小者胜"。
        var templates = mergedTemplates.Values
            .OrderBy(v => v.Template.Order).ThenBy(v => v.Template.Name, StringComparer.CurrentCulture)
            .Select(v => ValidateReferences(v.Template, v.Source, merged, problems))
            .ToList();

        ReportDefaultConflicts(templates, problems);

        return new FilterCatalog(list, templates, sources, problems);
    }

    /// <summary>校验并规范化一条定义。返回 null 表示这条不可用（原因记进 problems）。</summary>
    static FilterDefinition? Normalize(FilterDefinition raw, string source, List<string> problems)
    {
        var where = Path.GetFileName(source);

        var id = NormalizeId(raw.Id);
        if (id.Length == 0)
        {
            problems.Add($"{where}：有一条筛选器没有 id，已跳过");
            return null;
        }

        var name = (raw.Name ?? string.Empty).Trim();
        if (name.Length == 0) name = id;

        var extensions = CleanExtensions(raw.Extensions);
        var kinds = CleanKinds(raw.Kinds, id, where, problems);
        var subtypes = CleanTokens(raw.Subtypes);

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
            Subtypes = subtypes,
            NameRegex = regex,
            Glyph = string.IsNullOrWhiteSpace(raw.Glyph) ? null : raw.Glyph.Trim(),
        };

        // 只在"这条定义本身没问题、却一个条件都不剩"时才报空 ——
        // 否则写错一个类型名会同时收到"类型不认识"和"没有匹配条件"两条，
        // 后者是前者的后果，重复报只会让人以为有两处错误。
        if (def.IsEmpty && !problems.Any(p => p.Contains($"「{name}」") || p.Contains($"「{id}」")))
        {
            problems.Add($"{where}：筛选器「{name}」没有任何匹配条件（extensions / kinds / subtypes / namePattern 全空），已跳过");
            return null;
        }
        return def.IsEmpty ? null : def;
    }

    /// <summary>
    /// id 会进配置和日志，只留安全字符（"生信 相关" 这种写法自动变成 "生信-相关"）。
    /// 筛选器 id 与模板 id、以及模板里对筛选器的引用，都走这一套 —— 否则用户在模板里
    /// 按原样写 <c>"Bio Info"</c> 就会引用不到已被规范成 <c>bio-info</c> 的那条定义。
    /// </summary>
    static string NormalizeId(string? raw) =>
        new string((raw ?? string.Empty).Trim().ToLowerInvariant()
                   .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-')
                   .ToArray());

    /// <summary>后端 id 或 <c>"*"</c>（全局兜底）统一成小写去空；<c>"*"</c> 原样保留。</summary>
    static string NormalizeProviderToken(string? raw)
    {
        var v = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (v == "*") return "*";
        return new string(v.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray());
    }

    /// <summary>一串 id：规范化、去空、去重、保序。</summary>
    static List<string> CleanIdList(IReadOnlyList<string>? raw, bool providerTokens = false)
    {
        if (raw is null) return [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var item in raw)
        {
            var v = providerTokens ? NormalizeProviderToken(item) : NormalizeId(item);
            if (v.Length == 0 || !seen.Add(v)) continue;
            list.Add(v);
        }
        return list;
    }

    /// <summary>校验并规范化一个模板。返回 null 表示这条不可用（原因记进 problems）。</summary>
    static FilterTemplate? NormalizeTemplate(FilterTemplate raw, string source, List<string> problems)
    {
        var where = Path.GetFileName(source);

        var id = NormalizeId(raw.Id);
        if (id.Length == 0)
        {
            problems.Add($"{where}：有一个模板没有 id，已跳过");
            return null;
        }

        var name = (raw.Name ?? string.Empty).Trim();

        // 模板与筛选器不同：一条引用都没有是合法的（"极简"模板就是空标签栏），
        // 所以这里不像 FilterDefinition 那样把"空"当写错。
        return raw with
        {
            Id = id,
            Name = name.Length == 0 ? id : name,
            Filters = CleanIdList(raw.Filters),
            DefaultFor = CleanIdList(raw.DefaultFor, providerTokens: true),
            Providers = CleanIdList(raw.Providers, providerTokens: true),
        };
    }

    /// <summary>剔除指向不存在筛选器的引用（记进 problems，不崩、也不整条丢模板）。</summary>
    static FilterTemplate ValidateReferences(FilterTemplate t, string source,
                                             Dictionary<string, FilterDefinition> filters, List<string> problems)
    {
        var where = Path.GetFileName(source);
        var kept = new List<string>();
        foreach (var id in t.Filters)
        {
            if (!filters.ContainsKey(id))
            {
                problems.Add($"{where}：模板「{t.Name}」引用了不存在的筛选器「{id}」，已剔除该引用");
                continue;
            }
            kept.Add(id);
        }
        return kept.Count == t.Filters.Count ? t : t with { Filters = kept };
    }

    /// <summary>
    /// 两个模板都认领同一个后端为默认时，<b>order 小者胜</b>，并把冲突写进 problems ——
    /// 静默取一个的话，用户会看到"我明明写了 defaultFor 却不生效"，却查不出为什么。
    /// </summary>
    static void ReportDefaultConflicts(IReadOnlyList<FilterTemplate> templates, List<string> problems)
    {
        var winner = new Dictionary<string, FilterTemplate>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in templates)          // 调用方已按 order/name 排好，先到的就是胜者
            foreach (var provider in t.DefaultFor)
            {
                if (winner.TryGetValue(provider, out var first))
                    problems.Add($"模板「{t.Name}」与「{first.Name}」都声明是「{provider}」的默认模板，" +
                                 $"按 order 取「{first.Name}」");
                else
                    winner[provider] = t;
            }
    }

    /// <summary>
    /// 一串"自由取值"的 token：小写、去空、去重、保序。
    /// <para>
    /// 子类型这类值<b>不做白名单校验</b> —— 后端会不断加新子类型（Zotero 有 40 种条目类型），
    /// 我们不可能维护一份完整清单。写了不存在的值只是匹配不到，不是配置错误，
    /// 报出来只会让人以为文件写坏了。
    /// </para>
    /// </summary>
    static IReadOnlyList<string> CleanTokens(IReadOnlyList<string>? raw)
    {
        if (raw is null) return [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var item in raw)
        {
            var v = (item ?? string.Empty).Trim().ToLowerInvariant();
            if (v.Length == 0 || !seen.Add(v)) continue;
            list.Add(v);
        }
        return list;
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
    public IReadOnlyList<FilterDefinition> For(IReadOnlyList<string>? providerIds) =>
        All.Where(f => Visible(f, providerIds)).ToList();

    /// <summary>某个定义在该来源集合下该不该出现在标签栏里。</summary>
    static bool Visible(FilterDefinition f, IReadOnlyList<string>? providerIds) =>
        !f.Hidden &&
        (f.Providers.Count == 0 ||
         providerIds is not { Count: > 0 } ||
         f.Providers.Any(p => providerIds.Contains(p, StringComparer.OrdinalIgnoreCase)));

    // ── 模板（v2）──────────────────────────────────────────────────────────────
    // 模板只决定"标签栏里显示哪几个、按什么顺序"，不碰匹配语义；没有模板时全部回退到上面的平铺。

    /// <summary>
    /// 标签栏该显示哪些筛选器。
    /// <para>
    /// <paramref name="template"/> 为 null（或没写过 templates 节）= <b>现状平铺</b>，与
    /// <see cref="For"/> 逐项一致；给了模板 = 模板引用的 id ∩ <see cref="Visible"/>（双重显隐），
    /// 且<b>顺序以模板里的引用先后为准</b>（模板存在的意义就是"有序引用"）。
    /// </para>
    /// </summary>
    public IReadOnlyList<FilterDefinition> ForTemplate(FilterTemplate? template, IReadOnlyList<string>? providerIds)
    {
        if (template is null) return For(providerIds);

        var result = new List<FilterDefinition>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in template.Filters)
        {
            var def = Find(id);
            if (def is null || !Visible(def, providerIds)) continue;
            if (seen.Add(def.Id)) result.Add(def);
        }
        return result;
    }

    public FilterTemplate? FindTemplate(string? id) =>
        id is { Length: > 0 }
            ? Templates.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase))
            : null;

    /// <summary>某个后端下下拉里可选的模板（<c>providers</c> 留空 = 所有后端可选）；空/null 来源给全量。</summary>
    public IReadOnlyList<FilterTemplate> TemplatesFor(string? providerId) =>
        Templates.Where(t => t.AvailableFor(providerId)).ToList();

    /// <summary>
    /// 某个后端当前该用哪个模板。解析链（前一步落空才看下一步）：
    /// <list type="number">
    /// <item>用户<b>钉住</b>的（<c>settings.filterTemplates.&lt;providerId&gt;</c>）；</item>
    /// <item>部署级默认（<c>providers.&lt;id&gt;.options.filterTemplate</c>）；</item>
    /// <item>定义文件里 <c>defaultFor</c> 含该后端、order 最小者；</item>
    /// <item><c>defaultFor</c> 含 <c>"*"</c>、order 最小者；</item>
    /// <item>都没有 → <b>null = 回退现状平铺</b>。</item>
    /// </list>
    /// 认不出的 id（比如设置里钉的模板已被删掉）不生效，直接往下一步走 —— 它不该把标签栏变空。
    /// </summary>
    public FilterTemplate? ResolveTemplate(string? providerId,
                                           string? pinnedTemplateId = null,
                                           string? deploymentTemplateId = null) =>
        FindTemplate(pinnedTemplateId) ??
        FindTemplate(deploymentTemplateId) ??
        DefaultTemplate(providerId) ??
        DefaultTemplate("*");

    /// <summary>认领了该后端（或 <c>"*"</c>）为默认的模板里 order 最小的那个。Templates 已排序，取第一个即可。</summary>
    public FilterTemplate? DefaultTemplate(string? providerId) =>
        providerId is { Length: > 0 }
            ? Templates.FirstOrDefault(t => t.DefaultFor.Contains(providerId, StringComparer.OrdinalIgnoreCase))
            : null;

    public FilterDefinition? Find(string? id) =>
        id is { Length: > 0 } ? All.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase)) : null;
}
