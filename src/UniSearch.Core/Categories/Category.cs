using UniSearch.Sdk.Model;

namespace UniSearch.Core.Categories;

/// <summary>一个 UI 分类（顶部分段标签 + 结果分组）。</summary>
public sealed record CategoryDescriptor(
    string Id,
    string DisplayName,
    int Order,
    string Glyph,
    /// <summary>该分类为空时是否仍显示标签。</summary>
    bool ShowWhenEmpty = false,
    /// <summary>标签上的数字上限，超过显示 99+。</summary>
    int BadgeLimit = 999);

/// <summary>内置分类表。第三方 Provider 可通过 categories.json 追加，但不允许改变内置 Id 的语义。</summary>
public static class CategoryIds
{
    public const string All = "all";
    public const string Files = "files";
    public const string Folders = "folders";
    public const string Documents = "documents";
    public const string Images = "images";
    public const string Videos = "videos";
    public const string Music = "music";
    public const string Applications = "apps";
    public const string Archives = "archives";
    public const string Code = "code";
    public const string Literature = "literature";
    public const string Notes = "notes";
    public const string ContentMatches = "content";
    public const string Web = "web";
    public const string More = "more";

    public static readonly CategoryDescriptor[] Defaults =
    [
        new(All, "全部", 0, "\uE71D", ShowWhenEmpty: true),
        new(Files, "文件", 10, "\uE8A5"),
        new(Folders, "文件夹", 20, "\uE8B7"),
        new(Documents, "文档", 30, "\uE8A5"),
        new(Images, "图片", 40, "\uEB9F"),
        new(Videos, "视频", 50, "\uE786"),
        new(Music, "音乐", 60, "\uE8D6"),
        new(Applications, "应用", 70, "\uE71D"),
        new(Literature, "文献", 80, "\uE8F1"),
        new(Notes, "笔记", 90, "\uE70B"),
        new(ContentMatches, "正文命中", 100, "\uE85C"),
        new(Archives, "压缩包", 110, "\uF129"),
        new(Code, "代码", 120, "\uE943"),
        new(Web, "网络", 130, "\uE774"),
        new(More, "其他", 900, "\uE712"),
    ];

    public static CategoryDescriptor? Find(string? id) =>
        id is null ? null : Array.Find(Defaults, c => string.Equals(c.Id, id, StringComparison.Ordinal));

    /// <summary>扩展名 → 分类。与 Kind 冲突时 Kind 优先（见 CategoryEngine）。</summary>
    public static readonly IReadOnlyDictionary<string, string> ExtensionMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // 文档
            ["pdf"] = Documents, ["doc"] = Documents, ["docx"] = Documents, ["rtf"] = Documents,
            ["odt"] = Documents, ["xls"] = Documents, ["xlsx"] = Documents, ["ods"] = Documents,
            ["ppt"] = Documents, ["pptx"] = Documents, ["ofd"] = Documents, ["epub"] = Documents,
            ["mobi"] = Documents, ["azw3"] = Documents, ["caj"] = Documents, ["txt"] = Documents,
            ["md"] = Documents, ["markdown"] = Documents, ["tex"] = Documents, ["xps"] = Documents,
            // 图片
            ["jpg"] = Images, ["jpeg"] = Images, ["png"] = Images, ["gif"] = Images, ["bmp"] = Images,
            ["webp"] = Images, ["tif"] = Images, ["tiff"] = Images, ["ico"] = Images, ["svg"] = Images,
            ["heic"] = Images, ["heif"] = Images, ["raw"] = Images, ["cr2"] = Images, ["nef"] = Images,
            ["psd"] = Images, ["ai"] = Images, ["eps"] = Images,
            // 视频
            ["mp4"] = Videos, ["mkv"] = Videos, ["avi"] = Videos, ["mov"] = Videos, ["wmv"] = Videos,
            ["flv"] = Videos, ["webm"] = Videos, ["m4v"] = Videos, ["ts"] = Videos, ["rmvb"] = Videos,
            // 音频
            ["mp3"] = Music, ["flac"] = Music, ["wav"] = Music, ["m4a"] = Music, ["aac"] = Music,
            ["ogg"] = Music, ["opus"] = Music, ["wma"] = Music, ["ape"] = Music, ["mid"] = Music,
            // 压缩包
            ["zip"] = Archives, ["rar"] = Archives, ["7z"] = Archives, ["tar"] = Archives,
            ["gz"] = Archives, ["bz2"] = Archives, ["xz"] = Archives, ["iso"] = Archives, ["cab"] = Archives,
            // 代码
            ["cs"] = Code, ["py"] = Code, ["js"] = Code, ["ts"] = Code, ["jsx"] = Code, ["tsx"] = Code,
            ["java"] = Code, ["c"] = Code, ["h"] = Code, ["cpp"] = Code, ["hpp"] = Code, ["rs"] = Code,
            ["go"] = Code, ["rb"] = Code, ["php"] = Code, ["html"] = Code, ["css"] = Code, ["json"] = Code,
            ["xml"] = Code, ["yml"] = Code, ["yaml"] = Code, ["toml"] = Code, ["sql"] = Code, ["sh"] = Code,
            ["ps1"] = Code, ["bat"] = Code, ["cmd"] = Code, ["sln"] = Code, ["csproj"] = Code,
            // 应用
            ["exe"] = Applications, ["lnk"] = Applications, ["msi"] = Applications, ["appx"] = Applications,
            ["msix"] = Applications, ["bat"] = Applications,
        };

    /// <summary>结果类型 → 分类。</summary>
    public static readonly IReadOnlyDictionary<ResultKind, string> KindMap =
        new Dictionary<ResultKind, string>
        {
            [ResultKind.Folder] = Folders,
            [ResultKind.Application] = Applications,
            [ResultKind.Image] = Images,
            [ResultKind.Video] = Videos,
            [ResultKind.Audio] = Music,
            [ResultKind.Document] = Documents,
            [ResultKind.BibliographicItem] = Literature,
            [ResultKind.Note] = Notes,
            [ResultKind.Attachment] = Files,
            [ResultKind.Collection] = Folders,
            [ResultKind.Email] = More,
            [ResultKind.ChatMessage] = More,
            [ResultKind.Bookmark] = Web,
            [ResultKind.HistoryEntry] = Web,
            [ResultKind.WebPage] = Web,
            [ResultKind.CodeSymbol] = Code,
            [ResultKind.Setting] = Applications,
            [ResultKind.ArchiveEntry] = Archives,
            [ResultKind.Contact] = More,
            [ResultKind.AiAnswer] = More,
        };
}
