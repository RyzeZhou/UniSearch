namespace UniSearch.Sdk.Model;

/// <summary>
/// 结果的<b>语义类型</b>。Provider 只负责如实声明它找到了“什么东西”，
/// 永远不要由 Provider 决定该结果出现在 UI 的哪个分类里（那是 Core 的 CategoryEngine 的职责）。
/// </summary>
/// <remarks>
/// 新增成员属于向后兼容变更；<b>不得</b>重排或复用已有数值，因为分类规则配置会按名字持久化。
/// </remarks>
public enum ResultKind
{
    /// <summary>未知/自定义类型，Subtype 决定展示方式。</summary>
    Unknown = 0,

    /// <summary>普通文件（按扩展名进一步细分到 文档/图片/视频/音频/压缩包/代码…）。</summary>
    File = 1,

    /// <summary>目录。</summary>
    Folder = 2,

    /// <summary>可执行程序 / 应用启动器 / 快捷方式。</summary>
    Application = 3,

    /// <summary>文档的“正文命中”。注意：正文命中仍指向一个文件，用 <see cref="SearchResult.Path"/> 与 File 结果融合。</summary>
    Document = 4,

    /// <summary>图片（含 OCR 命中的图片）。</summary>
    Image = 5,

    Video = 6,
    Audio = 7,

    /// <summary>压缩包内条目（尚未展开时仍是 File）。</summary>
    ArchiveEntry = 8,

    // ---- 非文件系统实体：知识库 / 邮件 / 网页 等 ----

    /// <summary>文献条目（论文、书籍、章节…），例如 Zotero item。</summary>
    BibliographicItem = 20,

    /// <summary>笔记（Obsidian note、OneNote 页面…）。</summary>
    Note = 21,

    /// <summary>知识库附件，通常有本地文件路径，可与 File 结果融合。</summary>
    Attachment = 22,

    /// <summary>收藏夹 / 集合 / 分类目录（Zotero collection、邮件文件夹…）。</summary>
    Collection = 23,

    /// <summary>标签。</summary>
    Tag = 24,

    Email = 30,
    ChatMessage = 31,

    /// <summary>浏览器书签。</summary>
    Bookmark = 40,

    /// <summary>浏览历史条目。</summary>
    HistoryEntry = 41,

    /// <summary>网页 / URL（在线搜索引擎或本地缓存）。</summary>
    WebPage = 42,

    /// <summary>代码符号（函数/类/定义），例如本地 code search。</summary>
    CodeSymbol = 50,

    /// <summary>设置项（Windows Settings、应用内设置）。</summary>
    Setting = 60,

    /// <summary>联系人 / 人。</summary>
    Contact = 70,

    /// <summary>由本地 LLM/RAG 生成的合成答案。</summary>
    AiAnswer = 80,
}
