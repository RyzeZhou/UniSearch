using Microsoft.Win32;

namespace UniSearch.Providers.Anytxt;

/// <summary>
/// 找 AnyTXT 装在哪。<b>顺序有讲究</b>：注册表是权威来源，硬编码路径只是兜底 ——
/// 用户装到 D 盘是常态（本机就是 <c>D:\Program\Anytxt Searcher\</c>），写死 C 盘必然找不到。
/// </summary>
public static class AnytxtLocator
{
    public const string ExeName = "ATGUI.exe";

    /// <summary>索引服务名（AnyTXT 自己的 Windows 服务）。</summary>
    public const string ServiceName = "ATService";

    static string? _cached;
    static bool _probed;

    /// <summary>ATGUI.exe 的完整路径；找不到返回 null（不抛异常 —— 没装是正常情况）。</summary>
    public static string? FindExecutable()
    {
        if (_probed) return _cached;
        _probed = true;
        _cached = Probe();
        return _cached;
    }

    /// <summary>探测一次（不走缓存），供诊断用。</summary>
    public static string? Probe()
    {
        foreach (var dir in CandidateDirectories())
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var exe = Path.Combine(dir.TrimEnd('\\', '/'), ExeName);
                if (File.Exists(exe)) return exe;
            }
            catch
            {
                // 注册表里可能有畸形路径 —— 跳过，不要因为一条坏记录就放弃整个探测
            }
        }
        return null;
    }

    /// <summary>把"可能在哪儿"按可信度排序列出来。诊断时能看到都试过哪些。</summary>
    public static IEnumerable<string> CandidateDirectories()
    {
        // ① 卸载表（本机实测有 InstallLocation）
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            string? found = null;
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is not null)
                {
                    foreach (var sub in uninstall.GetSubKeyNames())
                    {
                        using var app = uninstall.OpenSubKey(sub);
                        var name = app?.GetValue("DisplayName") as string;
                        if (name is null || !name.Contains("nytxt", StringComparison.OrdinalIgnoreCase)) continue;

                        var loc = app?.GetValue("InstallLocation") as string;
                        if (!string.IsNullOrWhiteSpace(loc)) { found = loc; break; }

                        // 没有 InstallLocation 就从卸载命令反推目录
                        var uninstallCmd = app?.GetValue("UninstallString") as string;
                        if (!string.IsNullOrWhiteSpace(uninstallCmd))
                        {
                            var trimmed = uninstallCmd.Trim().Trim('"');
                            var slash = trimmed.LastIndexOf('\\');
                            if (slash > 0) { found = trimmed[..slash]; break; }
                        }
                    }
                }
            }
            catch
            {
                // 注册表读不动就跳过这一支
            }
            if (found is not null) yield return found;
        }

        // ② 索引服务的可执行路径（服务装了的话一定有）
        var svc = ReadServicePath();
        if (svc is not null)
        {
            var slash = svc.LastIndexOf('\\');
            if (slash > 0) yield return svc[..slash];
        }

        // ③ 常见安装位置（用户自定义目录时兜底）
        yield return @"C:\Program Files\Anytxt Searcher";
        yield return @"C:\Program Files (x86)\Anytxt Searcher";
        yield return @"D:\Program\Anytxt Searcher";
        yield return @"D:\Program Files\Anytxt Searcher";
    }

    /// <summary>
    /// 读服务注册表项里的 <c>ImagePath</c>。
    /// <b>不用 System.ServiceProcess</b>：那是额外包，而这里只需要读一个字符串。
    /// </summary>
    static string? ReadServicePath()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceName}");
            var image = key?.GetValue("ImagePath") as string;
            return string.IsNullOrWhiteSpace(image) ? null : image.Trim().Trim('"');
        }
        catch
        {
            return null;
        }
    }
}
