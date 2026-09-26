using UniSearch.Core.Archiving;
using Xunit;

namespace UniSearch.Tests;

/// <summary>
/// 压缩包规划（第 12 轮）：包名模板、重名冲突、zip 条目名。全是纯逻辑，不碰磁盘。
/// <para>
/// 这块必须测：多选压缩最容易出事的地方不是"压缩"，而是
/// ① 包名撞车（会不会覆盖用户已有的 zip）② zip 里同名条目互相覆盖（静默丢文件）。
/// </para>
/// </summary>
public class ArchivePlannerTests
{
    static readonly DateTime When = new(2026, 9, 26, 22, 30, 0);

    [Fact]
    public void Default_template_fills_parent_count_and_timestamp()
        => Assert.Equal("下载-3项-20260926-2230",
                        ArchivePlanner.BuildArchiveName(null, "下载", 3, When));

    [Fact]
    public void Empty_template_falls_back_to_default()
        => Assert.Equal("下载-3项-20260926-2230",
                        ArchivePlanner.BuildArchiveName("   ", "下载", 3, When));

    [Fact]
    public void Missing_parent_becomes_placeholder()
        => Assert.Equal("选中项-2项-20260926-2230",
                        ArchivePlanner.BuildArchiveName(null, null, 2, When));

    [Fact]
    public void Custom_template_is_honoured()
        => Assert.Equal("我的包-20260926", ArchivePlanner.BuildArchiveName("我的包-{yyyyMMdd}", "x", 9, When));

    [Fact]
    public void Illegal_characters_are_replaced()
    {
        var name = ArchivePlanner.BuildArchiveName("{parent}-{count}", "a:b*c?d", 1, When);
        Assert.DoesNotContain(':', name);
        Assert.DoesNotContain('*', name);
        Assert.DoesNotContain('?', name);
        Assert.StartsWith("a_b_c_d-1", name);
    }

    [Fact]
    public void Unique_file_keeps_name_when_free()
        => Assert.Equal(@"D:\x\a.zip", ArchivePlanner.EnsureUniqueFile(@"D:\x", "a.zip", _ => false));

    [Fact]
    public void Unique_file_appends_counter_on_collision()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"D:\x\a.zip", @"D:\x\a (2).zip" };
        Assert.Equal(@"D:\x\a (3).zip", ArchivePlanner.EnsureUniqueFile(@"D:\x", "a.zip", taken.Contains));
    }

    [Fact]
    public void Entries_use_file_names_when_unique()
    {
        var plan = ArchivePlanner.PlanEntries(new[] { @"D:\a\one.txt", @"D:\b\two.txt" });
        Assert.Equal(new[] { "one.txt", "two.txt" }, plan.Select(p => p.Entry));
    }

    [Fact]
    public void Same_name_from_different_dirs_gets_parent_prefix()
    {
        var plan = ArchivePlanner.PlanEntries(new[] { @"D:\a\same.txt", @"D:\b\same.txt" });
        Assert.Equal("same.txt", plan[0].Entry);
        Assert.Equal("b/same.txt", plan[1].Entry);   // 分隔符必须是 /（ZIP 规范，反斜杠会被当成文件名的一部分）
    }

    [Fact]
    public void Collision_on_prefixed_name_falls_back_to_counter()
    {
        // 第三个文件与前一个**父目录名相同**（D:\b 与 D:\x\b）→ 前缀方案 "b/same.txt" 也被占了，
        // 于是退到计数 "same (2).txt"。这条正是"绝不静默覆盖"的兜底路径。
        var plan = ArchivePlanner.PlanEntries(
            new[] { @"D:\a\same.txt", @"D:\b\same.txt", @"D:\x\b\same.txt" });
        Assert.Equal("same.txt", plan[0].Entry);
        Assert.Equal("b/same.txt", plan[1].Entry);
        Assert.Equal("same (2).txt", plan[2].Entry);
    }

    [Fact]
    public void Blank_paths_are_skipped()
    {
        var plan = ArchivePlanner.PlanEntries(new[] { "", "   ", @"D:\a\ok.txt" });
        Assert.Single(plan);
        Assert.Equal("ok.txt", plan[0].Entry);
    }

    [Fact]
    public void Source_paths_are_preserved_in_plan()
    {
        var plan = ArchivePlanner.PlanEntries(new[] { @"D:\a\one.txt" });
        Assert.Equal(@"D:\a\one.txt", plan[0].Source);
    }
}
