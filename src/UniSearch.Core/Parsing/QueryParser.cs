using System.Globalization;
using System.Text;
using UniSearch.Sdk.Model;

namespace UniSearch.Core.Parsing;

/// <summary>
/// 把用户输入解析成 <see cref="SearchQuery"/>。
/// <para>
/// <b>核心规则（来自 PowerToys CmdPal 的 Ext.Indexer "Query Handling Contract"，docs/research/REF-5 §2.1）</b>：
/// 输入看起来已经是某种后端的查询语法时，<b>一字不改地透传</b>；
/// 只有纯自由文本才做拆解、加隐式通配、翻成过滤器。
/// 否则用户打 <c>kind:folder</c>、<c>content:abc</c>、<c>(a|b)</c>、<c>C:\Users</c> 会被我们拆坏。
/// </para>
/// </summary>
public static class QueryParser
{
    /// <summary>我们认识的过滤器动词（会被结构化成 <see cref="QueryFilters"/>，同时保留透传串里的原样写法）。</summary>
    public static readonly string[] OwnFilterVerbs = ["ext", "kind", "type", "size", "dm", "dc", "regex", "re", "folder", "file"];

    /// <summary>
    /// <b>我们自己的</b>过滤器动词：解析成 <see cref="QueryFilters"/>（这样 AnyTXT / Windows 索引等
    /// 不懂 Everything 语法的后端也能得到同样语义），<b>不</b>触发整串透传。
    /// </summary>
    static readonly HashSet<string> OwnVerbSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "ext", "extension", "kind", "type", "size", "dm", "dc", "regex", "re", "folder", "file",
    };

    /// <summary>
    /// <b>后端专有</b>语法：出现即整串透传，Core 不再拆解、不再做词项后过滤。
    /// 只列已核实存在的 Everything 函数（docs/research/EVERYTHING-IPC-VERIFIED.md 表 F），
    /// 不含 <c>pic:</c>/<c>exe:</c>/<c>folderparent:</c> 这类**未证实**的写法。
    /// </summary>
    static readonly HashSet<string> ForeignVerbSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "content", "filelist", "loc", "location", "parent", "ancestor", "child", "descendant",
        "full-path", "path", "name", "app-name", "product-name", "company-name", "copyright",
        "description", "version", "attributes", "file-attributes", "attrib", "date-created",
        "date-modified", "date-accessed", "date-run", "run-count", "count", "audio-format",
        "video-format", "frame-rate", "bit-rate", "duration", "dimension", "width", "height",
        "is", "boolean", "diacritics", "nodiacritics", "case", "nocase", "wholeword", "noww",
        "nopath", "wildcards", "regexp", "prefix", "suffix", "punctuation", "whitespace",
        "drive", "volume", "ntfs", "usn", "junction", "hardlink", "symlink", "empty",
        "duplicates", "offline", "online", "list", "lists",
    };

    static readonly string[] AqsOperators = [" AND ", " OR ", " NOT "];

    public static SearchQuery Parse(long requestId, string raw, int resultBudget = 60)
    {
        raw ??= string.Empty;

        if (LooksStructured(raw))
            return new SearchQuery
            {
                RequestId = requestId,
                RawText = raw,
                Text = raw.Trim(),
                ProviderText = raw.Trim(),
                IsStructured = true,
                Terms = [],                       // 结构化时不做词项后过滤
                ResultBudget = resultBudget,
            };

        var text = raw;
        var ext = new List<string>();
        var kinds = new List<ResultKind>();
        string? phrase = null;
        bool foldersOnly = false, regex = false, wildcard = false;
        string? regexPattern = null;
        long? minSize = null, maxSize = null;
        DateTimeOffset? modifiedAfter = null;
        string? explicitDir = null;

        var m = PhraseRegexMatch(ref text);
        if (m is not null) phrase = m;

        var free = new List<string>();
        foreach (var tok in text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var body = tok.StartsWith('-') && tok.Length > 1 ? tok[1..] : tok;
            var colon = body.IndexOf(':');
            if (colon > 0 &&
                TryFilter(body[..colon].ToLowerInvariant(), body[(colon + 1)..], ext, kinds,
                          ref foldersOnly, ref regex, ref regexPattern, ref minSize, ref maxSize,
                          ref modifiedAfter, ref explicitDir))
                continue;

            if (body.Contains('*') || body.Contains('?')) wildcard = true;
            free.Add(tok);
        }

        var freeText = string.Join(' ', free).Trim();
        var terms = freeText
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var q = new SearchQuery
        {
            RequestId = requestId,
            RawText = raw,
            Text = freeText,
            Terms = terms,
            ResultBudget = resultBudget,
            Filters = new QueryFilters
            {
                Extensions = ext,
                Kinds = kinds,
                Phrase = phrase,
                FoldersOnly = foldersOnly,
                RegexRequested = regex,
                RegexPattern = regexPattern,
                MinSizeBytes = minSize,
                MaxSizeBytes = maxSize,
                ModifiedAfter = modifiedAfter,
                WildcardRequested = wildcard,
                ExplicitDirectory = explicitDir,
            },
        };
        return q with { ProviderText = Reconstruct(q) };
    }

    /// <summary>
    /// 把一组额外的过滤器并进查询，并<b>重建 <see cref="SearchQuery.ProviderText"/></b>。
    /// <para>
    /// 这是 UI 的"筛选器模板"（filters.json 里的自定义筛选器）落到查询上的唯一入口：
    /// 只改 <c>Filters</c> 而不重建 <c>ProviderText</c>，后端拿到的还是没过滤的查询串 ——
    /// 后端会把整库结果都搬过来再由 Core 后过滤，白烧一遍（几百条 vs 几十万条）。
    /// </para>
    /// </summary>
    public static SearchQuery WithFilters(SearchQuery q, QueryFilters filters)
        => q with { ProviderText = Reconstruct(q with { Filters = filters }) };

    static string? PhraseRegexMatch(ref string text)
    {
        var i = text.IndexOf('"');
        if (i < 0) return null;
        var j = text.IndexOf('"', i + 1);
        if (j <= i + 1) return null;
        var phrase = text[(i + 1)..j];
        text = text[..i] + " " + text[(j + 1)..];
        return phrase.Length == 0 ? null : phrase;
    }

    /// <summary>
    /// 判断输入是否"已经像后端查询语言"，从而必须整串透传。
    /// <para>
    /// 规则（保守：宁可漏判，不可把普通句子误判成语法）：
    /// <list type="number">
    /// <item><description>前导 <c>!</c> 取反（<c>!temp</c>）→ 透传；</description></item>
    /// <item><description>出现<b>后端专有</b>函数（<c>content:</c>、<c>parent:</c>、<c>full-path:</c>、元数据函数…）→ 透传；</description></item>
    /// <item><description>通配 <c>*</c> <c>?</c>、分组 <c>( )</c>、OR <c>|</c>、AQS 的 <c>AND/OR/NOT</c> → 透传；
    /// 但 <c>ext:pdf|docx</c> 这种把 <c>|</c> 用在<b>我们自己的过滤器</b>里的写法会被正常解析；</description></item>
    /// <item><description>裸路径（<c>C:\Users</c>、<c>\\srv\share</c>、以 <c>\</c> 结尾）→ 透传。</description></item>
    /// </list>
    /// </para>
    /// </summary>
    public static bool LooksStructured(string raw)
    {
        var s = raw.Trim();
        if (s.Length == 0) return false;

        // 盘符路径 / UNC（"C:\Users" 这种整体不是一个词项，拆了就废）
        if (s.Length >= 3 && char.IsLetter(s[0]) && s[1] == ':' && (s[2] == '\u005C' || s[2] == '/')) return true;
        if (s.StartsWith("\u005C\u005C", StringComparison.Ordinal)) return true;

        foreach (var op in AqsOperators)
            if (s.Contains(op, StringComparison.OrdinalIgnoreCase)) return true;

        foreach (var tok in s.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // 1) 取反
            if (tok.Length > 1 && tok[0] == '!') return true;

            var colon = tok.IndexOf(':');
            if (colon > 0 && colon < tok.Length - 1)
            {
                var verb = tok[..colon];
                if (OwnVerbSet.Contains(verb)) continue;        // ext:/kind:/size:… 归我们解析
                if (ForeignVerbSet.Contains(verb)) return true; // content:/parent:/… 透传
            }

            // 2) 通配与结构符（注意 size:>10mb 已在上面被 OwnVerbSet 拦下）
            if (tok.Contains('*') || tok.Contains('?')) return true;
            if (tok.Contains('(') || tok.Contains(')') || tok.Contains('|')) return true;

            // 3) 路径形状
            if (tok.Contains('\u005C') && (tok.EndsWith('\u005C') || tok.Contains(":\u005C", StringComparison.Ordinal)))
                return true;
        }
        return false;
    }

    static bool TryFilter(string verb, string val, List<string> ext, List<ResultKind> kinds,
        ref bool foldersOnly, ref bool regex, ref string? regexPattern,
        ref long? minSize, ref long? maxSize, ref DateTimeOffset? modifiedAfter, ref string? explicitDir)
    {
        if (val.Length == 0) return false;

        switch (verb)
        {
            case "ext":
                foreach (var e in val.Split(';', ',', '|'))
                {
                    var t = e.Trim().TrimStart('.').ToLowerInvariant();
                    if (t.Length > 0) ext.Add(t);
                }
                return ext.Count > 0;

            case "kind" or "type":
                if (val.Equals("folder", StringComparison.OrdinalIgnoreCase)) { foldersOnly = true; return true; }
                if (val.Equals("file", StringComparison.OrdinalIgnoreCase)) { kinds.Add(ResultKind.File); return true; }
                if (Enum.TryParse<ResultKind>(val, ignoreCase: true, out var k)) { kinds.Add(k); return true; }
                var alias = val.ToLowerInvariant() switch
                {
                    "doc" or "docs" => ResultKind.Document,
                    "pic" or "photo" => ResultKind.Image,
                    "vid" or "movie" => ResultKind.Video,
                    "app" or "apps" => ResultKind.Application,
                    "music" or "aud" => ResultKind.Audio,
                    "paper" or "papers" or "bib" => ResultKind.BibliographicItem,
                    "note" => ResultKind.Note,
                    "zip" or "archive" => ResultKind.ArchiveEntry,
                    _ => (ResultKind?)null,
                };
                if (alias is { } a) { kinds.Add(a); return true; }

                // 最后一种常见写法：用户直接写扩展名（kind:pdf / type:docx）→ 当成扩展名过滤
                var kindParts = val.Split(';', ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (kindParts.Length > 0 && kindParts.All(p => Categories.CategoryIds.ExtensionMap.ContainsKey(p.TrimStart('.'))))
                {
                    foreach (var p in kindParts) ext.Add(p.Trim().TrimStart('.').ToLowerInvariant());
                    return true;
                }
                return false;

            case "size":
                var parts = val.Split("..", StringSplitOptions.TrimEntries);
                if (parts.Length == 2 && TryBytes(parts[0], out var lo) && TryBytes(parts[1], out var hi))
                { minSize = lo; maxSize = hi; return true; }
                if (TryBytes(val.TrimStart('>', '<', '='), out var one))
                {
                    if (val.StartsWith('>')) minSize = one;
                    else if (val.StartsWith('<')) maxSize = one;
                    else { minSize = one; maxSize = one; }
                    return true;
                }
                return false;

            case "dm" or "dc":
                if (TryRelativeDate(val, out var dt)) { modifiedAfter = dt; return true; }
                return false;

            case "regex" or "re":
                regex = true; regexPattern = val; return true;

            case "parent" or "ancestor" or "path" or "loc" or "location":
                explicitDir = val.Trim('"'); return true;

            default:
                return false;
        }
    }

    static readonly (string Suffix, long Multiplier)[] SizeSuffixes =
    [
        ("tb", 1L << 40), ("gb", 1L << 30), ("mb", 1L << 20), ("kb", 1L << 10),
        ("t", 1L << 40), ("g", 1L << 30), ("m", 1L << 20), ("k", 1L << 10), ("b", 1L),
    ];

    public static bool TryBytes(string s, out long bytes)
    {
        bytes = 0;
        s = s.Trim().ToLowerInvariant();
        long mult = 1;
        foreach (var (suffix, multiplier) in SizeSuffixes)
        {
            if (s.EndsWith(suffix, StringComparison.Ordinal))
            {
                s = s[..^suffix.Length].Trim();
                mult = multiplier;
                break;
            }
        }
        if (s.Length == 0) return false;
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return false;
        bytes = (long)(v * mult);
        return true;
    }

    public static bool TryRelativeDate(string val, out DateTimeOffset dt)
    {
        dt = default;
        val = val.Trim().ToLowerInvariant();
        var now = DateTimeOffset.Now;
        var digits = val.TrimEnd('y', 'm', 'w', 'd', 'h');
        if (digits.Length != val.Length && int.TryParse(digits, out var n))
        {
            dt = val[^1] switch
            {
                'y' => now.AddYears(-n),
                'm' => now.AddMonths(-n),
                'w' => now.AddDays(-7 * n),
                'd' => now.AddDays(-n),
                'h' => now.AddHours(-n),
                _ => default,
            };
            if (dt != default) return true;
        }
        return DateTimeOffset.TryParse(val, CultureInfo.CurrentCulture, DateTimeStyles.None, out dt);
    }

    /// <summary>自由文本 → 中立表达式。后端各自决定信任到什么程度（Everything 直接吃这套语法）。</summary>
    public static string Reconstruct(SearchQuery q)
    {
        var sb = new StringBuilder();
        if (q.Text.Length > 0) sb.Append(q.Text);
        if (q.Filters.Phrase is { } p) sb.Append(" \"").Append(p).Append('"');
        if (q.Filters.Extensions.Count > 0) sb.Append(" ext:").Append(string.Join(';', q.Filters.Extensions));
        if (q.Filters.FoldersOnly) sb.Append(" folder:");
        foreach (var k in q.Filters.Kinds) sb.Append(" kind:").Append(k.ToString().ToLowerInvariant());
        if (q.Filters.MinSizeBytes is { } lo && q.Filters.MaxSizeBytes is { } hi)
            sb.Append(" size:").Append(lo).Append("..").Append(hi);
        else if (q.Filters.MinSizeBytes is { } onlyMin)
            sb.Append(" size:>").Append(onlyMin);
        if (q.Filters.ModifiedAfter is { } dm) sb.Append(" dm:").Append(dm.ToString("yyyy-MM-dd"));
        return sb.ToString().Trim();
    }
}
