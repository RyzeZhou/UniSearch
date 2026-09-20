namespace UniSearch.Sdk.Capabilities;

/// <summary>
/// 能力声明。Broker 用它做<b>调度匹配</b>而不是把所有查询广播给所有插件：
/// 例如 Explorer 内 <see cref="Model.ResultKind"/> 为文献的 Provider 会被跳过。
/// </summary>
[Flags]
public enum ProviderCapability : long
{
    None = 0,

    // —— 能返回什么 —— 
    ReturnsFiles = 1 << 0,
    ReturnsFolders = 1 << 1,
    ReturnsImages = 1 << 2,
    ReturnsVideos = 1 << 3,
    ReturnsAudio = 1 << 4,
    ReturnsDocuments = 1 << 5,
    ReturnsApplications = 1 << 6,
    ReturnsArchives = 1 << 7,
    ReturnsBibliographicItems = 1 << 8,
    ReturnsNotes = 1 << 9,
    ReturnsAttachments = 1 << 10,
    ReturnsEmails = 1 << 11,
    ReturnsWebBookmarks = 1 << 12,
    ReturnsSettings = 1 << 13,

    // —— 能搜什么层面 —— 
    /// <summary>按文件名/路径搜。</summary>
    SearchesFileName = 1 << 20,
    /// <summary>搜文件<b>正文</b>并可返回 snippet。</summary>
    SearchesFileContent = 1 << 21,
    /// <summary>搜结构化元数据（作者、标签、EXIF、ID3）。</summary>
    SearchesMetadata = 1 << 22,
    /// <summary>支持 <c>ext:</c> 之类的类型过滤。</summary>
    SupportsKindFilter = 1 << 23,
    /// <summary>支持把结果限定到某个目录。</summary>
    SupportsDirectoryScope = 1 << 24,
    /// <summary>支持全局（无目录）搜索。</summary>
    SupportsGlobalScope = 1 << 25,
    /// <summary>支持结果内二次过滤（within results）。</summary>
    SupportsRefine = 1 << 26,

    // —— 附加交互 —— 
    ProvidesPreview = 1L << 40,
    ProvidesActions = 1L << 41,
    ProvidesSuggestions = 1L << 42,
    /// <summary>可枚举“当前目录的内容”（Explorer 空查询时展示）。</summary>
    SupportsListing = 1L << 43,
}
