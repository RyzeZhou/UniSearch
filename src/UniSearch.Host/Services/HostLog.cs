using System.Diagnostics;
using System.IO;
using System.Windows;
using UniSearch.Sdk.Runtime;

namespace UniSearch.Host.Services;

/// <summary>宿主侧的日志门面（IProviderRuntime.Log）。MVP 只写 %LOCALAPPDATA%\UniSearch\host.log。</summary>
public sealed class HostLog : IUniSearchLog
{
    public HostLog(string logFile)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
              _log = File.AppendText(logFile); _log.AutoFlush = true; }
        catch { _log = null; }
    }

    readonly StreamWriter? _log;

    void Write(string level, string providerId, string message, Exception? error = null)
    {
        System.Diagnostics.Debug.WriteLine($"[UniSearch {level}] {providerId}: {message}");
        try
        {
            _log?.WriteLine($"{DateTime.Now:O} {level} [{providerId}] {message}{(error is null ? "" : " :: " + error)}");
        }
        catch { /* 日志失败不应当打断宿主 */ }
    }

    public void Debug(string providerId, string message) => Write("DBG", providerId, message);
    public void Info(string providerId, string message) => Write("INF", providerId, message);
    public void Warn(string providerId, string message, Exception? error = null) => Write("WRN", providerId, message, error);
    public void Error(string providerId, string message, Exception? error = null) => Write("ERR", providerId, message, error);

    public void Dispose() => _log?.Dispose();
}