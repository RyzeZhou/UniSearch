using System.Text.Json;
using UniSearch.Sdk.Model;

namespace UniSearch.Providers.Zotero;

/// <summary>
/// Zotero 的 JSON → 统一结果模型。<b>纯函数</b>，喂真实响应样例就能断言字段映射对不对。
/// <para>
/// 形状与 zotero.org Web API v3 同构：顶层是数组，每项 <c>{ key, version, links, meta, data }</c>，
/// 具体字段在 <c>data</c> 里。
/// </para>
/// </summary>
public static class ZoteroItemMapper
{
    /// <summary>把一次响应的 JSON 数组映射成结果行。<b>不抛异常</b>：坏行跳过，不拖垮整批。</summary>
    public static List<SearchResult> MapArray(JsonElement root, int? totalAvailable)
    {
        var list = new List<SearchResult>();
        if (root.ValueKind != JsonValueKind.Array) return list;

        foreach (var item in root.EnumerateArray())
        {
            var mapped = Map(item, totalAvailable);
            if (mapped is not null) list.Add(mapped);
        }
        return list;
    }

    /// <summary>映射单条。返回 null = 这条没有可用的 key，跳过。</summary>
    public static SearchResult? Map(JsonElement item, int? totalAvailable)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        if (!item.TryGetProperty("key", out var keyEl) || keyEl.GetString() is not { Length: > 0 } key) return null;
        if (!item.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return null;

        var itemType = Str(data, "itemType") ?? "document";
        var isAttachment = itemType == "attachment";
        var isNote = itemType == "note";

        var title = Str(data, "title");
        var localPath = isAttachment ? EnclosurePath(item) : null;

        if (string.IsNullOrWhiteSpace(title))
            title = localPath is { Length: > 0 } p ? Path.GetFileName(p) : key;

        var meta = BuildMetadata(item, data, itemType);
        var tags = BuildTags(data);

        var kind = isAttachment ? ResultKind.Attachment
                 : isNote ? ResultKind.Note
                 : ResultKind.BibliographicItem;

        return new SearchResult
        {
            ProviderId = ZoteroProvider.ProviderId,
            ProviderItemId = key,
            Kind = kind,
            Subtype = itemType,
            // 元数据命中（标题/作者/年份）与正文命中（qmode=everything 打中的 PDF）在 API 响应里
            // 区分不出来 —— 所以如实标 Metadata，不假装知道是哪种。
            Match = MatchKind.Metadata,
            Title = title!,
            Subtitle = BuildSubtitle(item, data, itemType, localPath),
            // 附件有真实本地路径 → 与 Everything / AnyTXT 的结果天然融合（同一个 PDF 只出现一行）。
            // 顶层条目没有路径 → 走 Uri 分支融合，不会跟文件结果撞车。
            Path = localPath,
            Uri = isAttachment && localPath is not null ? null : SelectUri(key),
            SizeBytes = AttachmentSize(item),
            ModifiedAt = ParseZoteroDate(Str(data, "dateModified")),
            CreatedAt = ParseZoteroDate(Str(data, "dateAdded")),
            Metadata = meta,
            Icon = new IconHint
            {
                ShellIconPath = localPath,
                IconByExtensionOnly = localPath is null,
                Glyph = localPath is null ? "\uE8F1" : null,   // 无路径的条目用文献字形
                Badge = isAttachment ? "PDF" : null,
            },
            Tags = tags,
            // 条目是只读的：UniSearch 不改 Zotero（Zotero 10 能写，但那需要用户逐次授权，超出本期）
            ReadOnly = true,
            // 没有本地文件的条目，预览面板给不出东西 —— 明说，而不是让预览空白着转圈
            DisablePreview = localPath is null,
            TotalAvailable = totalAvailable,
            CopyText = localPath ?? SelectUri(key),
        };
    }

    /// <summary>Zotero 的选中 URI。<c>select/library/items/&lt;KEY&gt;</c> 是官方文档里的形式。</summary>
    public static string SelectUri(string key) => $"zotero://select/library/items/{key}";

    /// <summary>
    /// 附件在磁盘上的真实路径。
    /// <c>links.enclosure.href</c> 是 <c>file:///C:/…/a%20b.pdf</c> 这种 URL，
    /// <b>必须解码</b>（中文名、空格都被百分号编码了），否则右键/预览都会找不到文件。
    /// </summary>
    internal static string? EnclosurePath(JsonElement item)
    {
        if (!item.TryGetProperty("links", out var links) || links.ValueKind != JsonValueKind.Object) return null;
        if (!links.TryGetProperty("enclosure", out var enc) || enc.ValueKind != JsonValueKind.Object) return null;
        var href = Str(enc, "href");
        if (string.IsNullOrWhiteSpace(href)) return null;

        try
        {
            var uri = new Uri(href);
            return uri.IsFile ? uri.LocalPath : href;
        }
        catch
        {
            return null;   // 畸形 URL 不该让整条结果消失
        }
    }

    /// <summary>附件的字节数（在 <c>links.enclosure.length</c> 或 <c>links.attachment.attachmentSize</c>）。</summary>
    internal static long? AttachmentSize(JsonElement item)
    {
        if (!item.TryGetProperty("links", out var links) || links.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in new[] { "enclosure", "attachment" })
        {
            if (!links.TryGetProperty(name, out var node) || node.ValueKind != JsonValueKind.Object) continue;
            foreach (var field in new[] { "length", "attachmentSize" })
            {
                if (node.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n))
                    return n;
            }
        }
        return null;
    }

    /// <summary>副标题走"作者 · 年份 · 期刊"这种一眼能读的形式（同 <c>SearchResult.Subtitle</c> 的注释）。</summary>
    internal static string? BuildSubtitle(JsonElement item, JsonElement data, string itemType, string? localPath)
    {
        if (localPath is { Length: > 0 } p) return Path.GetDirectoryName(p);

        var parts = new List<string>(3);

        var creator = CreatorSummary(item, data);
        if (creator is { Length: > 0 }) parts.Add(creator);

        var year = Year(Str(data, "date"));
        if (year is { Length: > 0 }) parts.Add(year);

        var venue = Str(data, "publicationTitle") ?? Str(data, "journalAbbreviation")
                    ?? Str(data, "bookTitle") ?? Str(data, "publisher");
        if (venue is { Length: > 0 }) parts.Add(venue);

        return parts.Count > 0 ? string.Join(" · ", parts) : itemType;
    }

    /// <summary>
    /// 作者摘要。<b>优先用 Zotero 自己算好的 <c>meta.creatorSummary</c></b>（"Jung 等"这种），
    /// 它跟着 Zotero 的界面语言与引用风格走；我们自己拼的只是它缺席时的兜底。
    /// 两边算法不一致的话，同一个条目在 Zotero 里和在 UniSearch 里会显示成两个样子。
    /// </summary>
    internal static string? CreatorSummary(JsonElement item, JsonElement data)
    {
        if (item.ValueKind == JsonValueKind.Object &&
            item.TryGetProperty("meta", out var meta) &&
            Str(meta, "creatorSummary") is { Length: > 0 } fromZotero)
            return fromZotero;

        if (!data.TryGetProperty("creators", out var creators) || creators.ValueKind != JsonValueKind.Array)
            return null;

        var names = new List<string>();
        foreach (var c in creators.EnumerateArray())
        {
            if (c.ValueKind != JsonValueKind.Object) continue;
            // 摘要只取姓：Zotero 的 creatorSummary 也是这么做的
            var name = Str(c, "lastName") ?? Str(c, "firstName") ?? Str(c, "name");
            if (!string.IsNullOrWhiteSpace(name)) names.Add(name!);
            if (names.Count >= 2) break;
        }

        return names.Count switch
        {
            0 => null,
            1 => names[0],
            _ => names[0] + " 等",
        };
    }

    /// <summary>从 <c>date</c> 里取四位年份。Zotero 的日期是自由文本（<c>2026-06-17</c> / <c>11/2025</c> / <c>2026</c>）。</summary>
    internal static string? Year(string? date)
    {
        if (string.IsNullOrWhiteSpace(date)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(date, @"(1[5-9]\d{2}|20\d{2})");
        return m.Success ? m.Value : null;
    }

    /// <summary>Zotero 的时间戳形如 <c>2026-09-15T03:27:32Z</c>（UTC）。</summary>
    internal static DateTimeOffset? ParseZoteroDate(string? value)
        => DateTimeOffset.TryParse(value, null, System.Globalization.DateTimeStyles.AdjustToUniversal,
                                   out var parsed) ? parsed : null;

    static IReadOnlyDictionary<string, string> BuildMetadata(JsonElement item, JsonElement data, string itemType)
    {
        var meta = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["itemType"] = itemType,
        };

        void Put(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) meta[key] = value!;
        }

        Put("title", Str(data, "title"));
        Put("author", CreatorSummary(item, data));
        Put("year", Year(Str(data, "date")));
        Put("date", Str(data, "date"));
        Put("venue", Str(data, "publicationTitle") ?? Str(data, "bookTitle") ?? Str(data, "proceedingsTitle"));
        Put("doi", Str(data, "DOI"));
        Put("url", Str(data, "url"));
        Put("abstract", Str(data, "abstractNote"));
        Put("volume", Str(data, "volume"));
        Put("issue", Str(data, "issue"));
        Put("pages", Str(data, "pages"));

        var tagNames = TagNames(data);
        if (tagNames.Count > 0) meta["tags"] = string.Join(", ", tagNames);

        return meta;
    }

    internal static List<string> TagNames(JsonElement data)
    {
        var list = new List<string>();
        if (!data.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array) return list;
        foreach (var t in tags.EnumerateArray())
        {
            var name = t.ValueKind == JsonValueKind.Object ? Str(t, "tag") : t.GetString();
            if (!string.IsNullOrWhiteSpace(name)) list.Add(name!);
        }
        return list;
    }

    /// <summary>
    /// 结果行上的小标签。<b>来源徽标永远在</b>（"这条是 Zotero 给的"是每行都该有的信息），
    /// 后面再跟最多 3 个 Zotero 标签 —— 标签是 Zotero 最好用的筛选维度，值得露出来。
    /// </summary>
    static IReadOnlyList<ResultTag> BuildTags(JsonElement data)
    {
        var names = TagNames(data);
        var list = new List<ResultTag>(Math.Min(names.Count, 3) + 2) { new("Zotero", ResultTagTone.Accent) };
        foreach (var n in names.Take(3)) list.Add(new ResultTag(n, ResultTagTone.Neutral));
        if (names.Count > 3) list.Add(new ResultTag($"+{names.Count - 3}", ResultTagTone.Neutral));
        return list;
    }

    static string? Str(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
