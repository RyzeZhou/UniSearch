using System.IO;

namespace UniSearch.Core.Archiving;

/// <summary>
/// 压缩包的**命名与条目规划** —— 纯逻辑，可单测；真正写 zip 的 I/O 留给宿主层
/// （<c>UniSearch.Host.Services.ArchiveService</c>）。
/// <para>
/// 为什么要单独抽一层：多选压缩最容易出错的地方不是"压缩"本身，而是
/// ① 包名怎么起（含非法字符、重名怎么办）② zip 里的条目名怎么定
/// （来自不同目录的同名文件会**互相覆盖**，静默丢文件是最坏的结果）。
/// 这两件事都是纯函数，必须能被断言。
/// </para>
/// </summary>
public static class ArchivePlanner
{
    /// <summary>默认包名模板。变量：<c>{parent}</c> <c>{count}</c> <c>{yyyyMMdd-HHmm}</c> <c>{yyyyMMdd}</c>。</summary>
    public const string DefaultNameTemplate = "{parent}-{count}项-{yyyyMMdd-HHmm}";

    /// <summary>按模板生成压缩包文件名（<b>不含</b> .zip 扩展名）。非法字符一律换成下划线。</summary>
    public static string BuildArchiveName(string? template, string? parentName, int count, DateTime now)
    {
        var t = string.IsNullOrWhiteSpace(template) ? DefaultNameTemplate : template;
        var parent = string.IsNullOrWhiteSpace(parentName) ? "选中项" : parentName;

        var name = t.Replace("{parent}", parent)
                    .Replace("{count}", count.ToString())
                    .Replace("{yyyyMMdd-HHmm}", now.ToString("yyyyMMdd-HHmm"))
                    .Replace("{yyyyMMdd}", now.ToString("yyyyMMdd"));

        foreach (var bad in Path.GetInvalidFileNameChars())
            name = name.Replace(bad, '_');

        name = name.Trim().TrimEnd('.');
        return name.Length == 0 ? "压缩包" : name;
    }

    /// <summary>
    /// 目标文件已存在时改名：<c>a.zip</c> → <c>a (2).zip</c>（与资源管理器同款口径）。
    /// <paramref name="exists"/> 注入是为了单测（不碰真实磁盘）。
    /// </summary>
    public static string EnsureUniqueFile(string directory, string fileName, Func<string, bool> exists)
    {
        var candidate = Path.Combine(directory, fileName);
        if (!exists(candidate)) return candidate;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (int i = 2; i < 1000; i++)
        {
            candidate = Path.Combine(directory, $"{stem} ({i}){ext}");
            if (!exists(candidate)) return candidate;
        }
        // 极端情况（同名 1000 个）：加随机后缀，绝不返回一个已存在的路径去覆盖
        return Path.Combine(directory, $"{stem} ({Guid.NewGuid():N}){ext}");
    }

    /// <summary>
    /// 规划 zip 内的条目名。
    /// <list type="bullet">
    /// <item>默认只用文件名（解压出来是平铺的，符合"选中几个文件打个包"的直觉）；</item>
    /// <item>**同名冲突**（来自不同目录）→ 改成 <c>父目录名/文件名</c>；</item>
    /// <item>再冲突 → 加序号 <c>名字 (2).ext</c>；</item>
    /// <item>分隔符统一用 <c>/</c> —— ZIP 规范如此，用反斜杠会被部分解压工具当成文件名的一部分。</item>
    /// </list>
    /// 绝不静默覆盖：用户以为"全压进去了"而实际少了几个，是这类功能最坏的结果。
    /// </summary>
    public static IReadOnlyList<(string Source, string Entry)> PlanEntries(IEnumerable<string> paths)
    {
        var plan = new List<(string Source, string Entry)>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var path = raw.TrimEnd('\\', '/');
            if (path.Length == 0) continue;

            var name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(name)) name = path;   // 盘根（D:\）这类没有文件名的

            var entry = name;
            if (!used.Add(entry))
            {
                var parent = Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty);
                var withParent = string.IsNullOrEmpty(parent) ? name : $"{parent}/{name}";
                if (!used.Add(withParent))
                {
                    var stem = Path.GetFileNameWithoutExtension(name);
                    var ext = Path.GetExtension(name);
                    for (int i = 2; ; i++)
                    {
                        var candidate = $"{stem} ({i}){ext}";
                        if (used.Add(candidate)) { withParent = candidate; break; }
                    }
                }
                entry = withParent;
            }

            plan.Add((path, entry));
        }

        return plan;
    }
}
