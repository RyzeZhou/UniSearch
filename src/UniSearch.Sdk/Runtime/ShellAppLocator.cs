using Microsoft.Win32;

namespace UniSearch.Sdk.Runtime;

/// <summary>
/// 按"软件名 / 可执行名"在系统里认出一个外部软件 —— <b>不依赖写死的安装路径</b>。
/// <para>
/// 两级<b>系统事实</b>，逐级降级（都查不到返回 null，调用方走自己的候选表/字形兜底）：
/// <list type="number">
/// <item><b>开始菜单快捷方式</b>（ProgramData 与当前用户两处根）—— 便携版、绿色版只要
/// 建过快捷方式就在这；返回 .lnk 本身，抽图标和 ShellExecute 打开都会自动解引用目标。</item>
/// <item><b>App Paths 注册表</b>（HKLM 优先、HKCU 兜底）—— 正规安装器普遍会写，返回目标 exe。</item>
/// </list>
/// </para>
/// <para>
/// <b>为什么图标走探测而不是打包图标文件</b>：软件图标是各家的美术资产，打进我们的分发
/// 产物有许可问题；运行时从用户自己装的软件上抽，与"结果行抽文件图标"是同一性质。
/// </para>
/// <para>配套的图标声明见 <c>UniSearch.Sdk.Capabilities.ProviderDescriptor.Icon</c>（B4）。</para>
/// </summary>
public static class ShellAppLocator
{
    static readonly string[] StartMenuRoots =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
    ];

    /// <summary>
    /// 在开始菜单里找文件名含 <paramref name="keyword"/> 的快捷方式（不区分大小写）。
    /// 两处根都扫；同多个命中取<b>修改时间最新</b>的 —— 避开"卸载 xxx.lnk"和旧版本残留。
    /// 目录不存在（未配置 CommonStartMenu 的精简系统）按无结果处理，不算错。
    /// </summary>
    public static string? FindStartMenuShortcut(string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return null;

        string? best = null;
        var bestTime = DateTime.MinValue;
        foreach (var root in StartMenuRoots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    MatchCasing = MatchCasing.CaseInsensitive,
                };
                foreach (var lnk in Directory.EnumerateFiles(root, "*.lnk", options))
                {
                    if (!Path.GetFileNameWithoutExtension(lnk).Contains(keyword, StringComparison.OrdinalIgnoreCase))
                        continue;
                    // "卸载/Uninstall"开头的快捷方式指向的是卸载器，不是软件本身
                    var name = Path.GetFileNameWithoutExtension(lnk);
                    if (name.StartsWith("卸载", StringComparison.Ordinal) ||
                        name.StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var t = File.GetLastWriteTimeUtc(lnk);
                    if (t > bestTime) { bestTime = t; best = lnk; }
                }
            }
            catch { /* 单个根扫不动就跳过，另一个根还可能命中 */ }
        }
        return best;
    }

    /// <summary>
    /// 查 App Paths 注册表（<c>HKLM → HKCU</c>），返回目标 exe 路径。
    /// 键的默认值可能带引号、可能是相对的 —— 规范化成完整路径再用。
    /// </summary>
    public static string? FindViaAppPaths(string executableName)
    {
        if (string.IsNullOrWhiteSpace(executableName)) return null;

        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + executableName);
                if (key?.GetValue(null) is not string raw || raw.Length == 0) continue;

                // 默认值可能带引号；写成相对路径的极罕见，那种直接放弃（还有别的探测级兜着）
                var path = raw.Trim().Trim('"');
                if (path.Length == 0 || !Path.IsPathRooted(path)) continue;
                if (File.Exists(path)) return path;
            }
            catch { /* 注册表读不动就试下一个根 */ }
        }
        return null;
    }
}
