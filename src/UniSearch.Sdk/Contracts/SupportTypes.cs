namespace UniSearch.Sdk.Contracts;

using UniSearch.Sdk.Model;

/// <summary>预览请求。</summary>
public sealed record PreviewRequest(long RequestId, int Width, int Height, bool WantsThumbnails);

/// <summary>预览面板内容。宿主按 <see cref="Kind"/> 选择渲染器；一个预览可以同时带缩略图与元数据表。</summary>
public sealed record PreviewContent
{
    public required PreviewKind Kind { get; init; }
    public string? Title { get; init; }
    /// <summary>Kind=Text/Markdown/Html 时的正文（≤ 64KB，超出请自行截断）。</summary>
    public string? Text { get; init; }
    /// <summary>Kind=Image 时的本地图片路径。</summary>
    public string? ImagePath { get; init; }
    /// <summary>元数据表（键值对，按声明顺序显示）。</summary>
    public IReadOnlyList<PreviewField> Fields { get; init; } = [];
    /// <summary>该后端独有的诊断信息，Ctrl+Shift+I 才展示。</summary>
    public IReadOnlyDictionary<string, string> Diagnostics { get; init; } = SearchResult.EmptyMetadata;

    public static PreviewContent AsText(string? title, string body) =>
        new() { Kind = PreviewKind.Text, Title = title, Text = body };
    public static PreviewContent FieldsOnly(string title, IReadOnlyList<PreviewField> fields) =>
        new() { Kind = PreviewKind.Fields, Title = title, Fields = fields };
}

public enum PreviewKind { Empty, Text, Markdown, Html, Image, Fields, BinarySummary, Unsupported }

public sealed record PreviewField(string Label, string? Value, bool Monospace = false);

/// <summary>Provider 声明的领域动作（通用动作如“打开/打开所在目录/复制路径”由宿主统一注入，不要重复声明）。</summary>
public sealed record ResultAction
{
    /// <summary>Provider 内唯一，例如 <c>"zotero.export-bibtex"</c>。</summary>
    public required string Id { get; init; }
    public required string Label { get; init; }
    public string? Glyph { get; init; }
    public ResultActionGroup Group { get; init; } = ResultActionGroup.Provider;
    /// <summary>快捷键提示（仅展示，实际绑定由 UI 层集中管理）。</summary>
    public string? AcceleratorText { get; init; }
    /// <summary>需要 Shift 才出现（危险或低频动作）。</summary>
    public bool HiddenByDefault { get; init; }
    /// <summary>需要二次确认。</summary>
    public bool Destructive { get; init; }
}

public enum ResultActionGroup { Primary, OpenWith, Shell, Copy, Share, Provider }

public sealed record ActionResult(bool Success, string? Message = null, bool Handled = true)
{
    public static ActionResult Ok() => new(true);
    public static ActionResult Fail(string message) => new(false, message);
    /// <summary>Provider 不处理，交回宿主按默认动作执行（打开文件等）。</summary>
    public static ActionResult NotHandled() => new(false, null, Handled: false);
}

/// <summary>自动补全建议条目。</summary>
public sealed record SuggestionGroup(string Title, string? Glyph, IReadOnlyList<Suggestion> Items);

public sealed record Suggestion(string Text, string? Detail, SuggestionKind Kind)
{
    /// <summary>接受建议后写回搜索框的完整文本（默认 = Text）。</summary>
    public string? Replacement { get; init; }
}

public enum SuggestionKind { History, FilePath, FolderPath, Command, Filter, Provider, Knowledge }
