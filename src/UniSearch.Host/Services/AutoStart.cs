using Microsoft.Win32;
using UniSearch.Sdk.Runtime;

namespace UniSearch.Host.Services;

/// <summary>
/// 开机自启动（MVP A3）：HKCU Run 键增删。
/// <para>
/// 用当前 exe 路径（<see cref="Environment.ProcessPath"/>），dist 与 bin 都能用；
/// 路径带引号（Program Files 这类带空格路径不加引号会启动失败）。
/// 注册表写失败只记日志不抛 —— 开机项写不进去不该让设置保存整体失败。
/// </para>
/// </summary>
public static class AutoStart
{
    const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "UniSearch";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) is not null;
        }
        catch { return false; }
    }

    public static void Apply(bool enable, IUniSearchLog? log)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null) { log?.Warn("autostart", "打不开 Run 键"); return; }

            if (enable)
            {
                var exe = Environment.ProcessPath
                    ?? System.IO.Path.Combine(AppContext.BaseDirectory, "UniSearch.exe");
                key.SetValue(ValueName, $"\"{exe}\"", RegistryValueKind.String);
                log?.Info("autostart", $"开机自启已开：{exe}");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                log?.Info("autostart", "开机自启已关（Run 键已删）");
            }
        }
        catch (Exception ex)
        {
            log?.Warn("autostart", $"写 Run 键失败：{ex.Message}");
        }
    }
}
