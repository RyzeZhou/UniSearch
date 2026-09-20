using UniSearch.Sdk.Model;

namespace UniSearch.Core.Matching;

/// <summary>
/// 统一的名称匹配打分。Core 用它做后过滤与排序，Provider 也可以复用，
/// 从而保证“同一个词在 Everything 结果和 Zotero 结果里的一致程度可比”。
/// 输出恒在 [0,1]。
/// </summary>
public static class NameMatcher
{
    public const double Exact = 1.0;
    public const double Prefix = 0.90;
    public const double WordBoundary = 0.74;
    public const double Substring = 0.56;
    public const double Subsequence = 0.38;
    public const double NoMatch = -1;

    /// <summary>整串匹配（用户在搜索框里打的一整个词组）。</summary>
    public static double Score(string title, string needle)
    {
        if (string.IsNullOrEmpty(needle)) return 0.5;           // 空前缀 = 浏览，中性分
        if (string.IsNullOrEmpty(title)) return NoMatch;

        var t = title.AsSpan();
        var n = needle.AsSpan();

        if (t.Equals(n, StringComparison.OrdinalIgnoreCase)) return Exact;
        if (t.StartsWith(n, StringComparison.OrdinalIgnoreCase)) return Prefix;

        // 词边界：文件名 transformer_attention 命中 transformer；"PyTorch Manual" 命中 "torch"
        if (StartsAtWordBoundary(t, n)) return WordBoundary;

        if (ContainsIgnoreCase(t, n)) return Substring;

        return IsSubsequence(t, n) ? Subsequence : NoMatch;
    }

    /// <summary>多词项查询：取各词项得分的平均，任一词项完全不匹配则整体不匹配（AND 语义）。</summary>
    public static double ScoreTerms(string title, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0) return 0.5;
        double sum = 0;
        foreach (var term in terms)
        {
            var s = Score(title, term);
            if (s < 0) return NoMatch;
            sum += s;
        }
        return sum / terms.Count;
    }

    /// <summary>由得分反推 <see cref="MatchKind"/>，供 Provider 与 Core 共用。</summary>
    public static MatchKind KindFromScore(double score) => score switch
    {
        >= Exact - 1e-9 => MatchKind.ExactName,
        >= Prefix - 1e-9 => MatchKind.NamePrefix,
        >= WordBoundary - 1e-9 => MatchKind.NameWord,
        > 0 => MatchKind.NameFuzzy,
        _ => MatchKind.None,
    };

    /// <summary>标题是否命中过滤条件（用于 Core 的“Provider 不支持该过滤器时”的后过滤）。</summary>
    public static bool MatchesAll(string haystack, IReadOnlyList<string> terms)
    {
        foreach (var t in terms)
        {
            if (Score(haystack, t) < 0) return false;
        }
        return true;
    }

    static bool StartsAtWordBoundary(ReadOnlySpan<char> haystack, ReadOnlySpan<char> needle)
    {
        if (needle.Length == 0 || needle.Length > haystack.Length) return false;
        var start = 0;
        while (start <= haystack.Length - needle.Length)
        {
            var idx = haystack[start..].IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return false;                 // 注意：找不到必须直接返回，
            var i = start + idx;                       // 否则 start 不前进会变成死循环
            if (i == 0 || !IsWordChar(haystack[i - 1])) return true;
            start = i + 1;
        }
        return false;
    }

    static bool ContainsIgnoreCase(ReadOnlySpan<char> haystack, ReadOnlySpan<char> needle) =>
        haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

    static bool IsSubsequence(ReadOnlySpan<char> haystack, ReadOnlySpan<char> needle)
    {
        if (needle.Length == 0) return true;
        if (needle.Length > 12 || haystack.Length > 260) return false;   // 成本控制：模糊只用于短查询
        var p = 0;
        foreach (var c in haystack)
        {
            if (char.ToUpperInvariant(c) == char.ToUpperInvariant(needle[p]))
            {
                p++;
                if (p == needle.Length) return true;
            }
        }
        return false;
    }

    // 词边界 = 非字母数字。下划线/连字符/点是文件名里的常见分隔符（my_transformer_notes.md），
    // 必须当作边界，否则“transformer”只会被判成子串匹配，排序明显偏低。
    static bool IsWordChar(char c) => char.IsLetterOrDigit(c);

    /// <summary>
    /// 把命中片段包成 <c>[[term]]</c> 形式供 UI 高亮。Provider 若已自带高亮标记可跳过此步。
    /// </summary>
    public static string? Highlight(string? text, IReadOnlyList<string> terms)
    {
        if (string.IsNullOrEmpty(text) || terms.Count == 0) return text;
        var outSpan = new System.Text.StringBuilder(text.Length + 16);
        var lower = text.ToLowerInvariant();
        var i = 0;
        while (i < text.Length)
        {
            var bestLen = 0;
            foreach (var t in terms)
            {
                if (t.Length > bestLen && i + t.Length <= text.Length &&
                    string.CompareOrdinal(lower.Substring(i, t.Length), t) == 0)
                {
                    bestLen = t.Length;
                }
            }
            if (bestLen > 0)
            {
                outSpan.Append("[[").Append(text, i, bestLen).Append("]]");
                i += bestLen;
            }
            else
            {
                outSpan.Append(text[i]);
                i++;
            }
        }
        return outSpan.ToString();
    }
}
