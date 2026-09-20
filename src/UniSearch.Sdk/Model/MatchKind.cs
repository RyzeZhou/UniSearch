namespace UniSearch.Sdk.Model;

/// <summary>命中方式。参与排序，也决定 UI 上“内容命中”的展示形态。</summary>
[Flags]
public enum MatchKind
{
    None = 0,

    /// <summary>文件名/标题完全等于查询。</summary>
    ExactName = 1,

    /// <summary>文件名/标题前缀匹配。</summary>
    NamePrefix = 1 << 1,

    /// <summary>文件名/标题包含（词边界）。</summary>
    NameWord = 1 << 2,

    /// <summary>文件名子串/模糊匹配。</summary>
    NameFuzzy = 1 << 3,

    /// <summary>文件<b>正文</b>命中（AnyTXT / Zotero 全文 / 代码搜索）。</summary>
    Content = 1 << 8,

    /// <summary>元数据命中：作者、标签、摘要、EXIF、ID3、front-matter…</summary>
    Metadata = 1 << 9,

    /// <summary>路径命中（命中的是目录名而非文件名）。</summary>
    Path = 1 << 10,

    /// <summary>来自 MRU / 使用频率，而非查询词命中。</summary>
    Usage = 1 << 11,
}
