using System.IO;
using System.IO.Compression;

namespace UniSearch.Host.Services;

/// <summary>
/// 把一批本地路径写进一个 zip。<b>不做线程调度</b> —— 调用方负责放到后台线程上跑
/// （压缩大目录可能几秒到几十秒，绝不能压在 UI 线程上）。
/// <para>
/// 规划（包名、条目名、重名冲突）在 <c>UniSearch.Core.Archiving.ArchivePlanner</c> 里，
/// 这里只做 I/O。
/// </para>
/// </summary>
public static class ArchiveService
{
    /// <summary>
    /// 按规划写 zip。目录**递归**加入（条目名保留目录内的相对结构）；
    /// 单个条目失败不中断整包，结果里带失败清单 —— "部分成功却报成功"是这个项目明确禁止的。
    /// 返回 (写入条目数, 失败清单)。
    /// </summary>
    public static (int Added, IReadOnlyList<string> Failures) CreateZip(
        IReadOnlyList<(string Source, string Entry)> plan, string zipPath, CancellationToken token = default)
    {
        var failures = new List<string>();
        int added = 0;

        var dir = Path.GetDirectoryName(zipPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var (source, entry) in plan)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (Directory.Exists(source))
                {
                    var any = false;
                    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                    {
                        token.ThrowIfCancellationRequested();
                        var rel = Path.GetRelativePath(source, file).Replace('\\', '/');
                        zip.CreateEntryFromFile(file, $"{entry}/{rel}", CompressionLevel.Optimal);
                        added++;
                        any = true;
                    }
                    // 空目录：写一条以 "/" 结尾的条目，解压时才不会凭空少一个文件夹
                    if (!any) zip.CreateEntry(entry.TrimEnd('/') + "/");
                }
                else if (File.Exists(source))
                {
                    zip.CreateEntryFromFile(source, entry, CompressionLevel.Optimal);
                    added++;
                }
                else
                {
                    failures.Add($"{source}（不存在）");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { failures.Add($"{source}：{ex.Message}"); }
        }

        return (added, failures);
    }
}
