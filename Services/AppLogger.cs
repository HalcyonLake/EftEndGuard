using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace EftEndGuard.Services;

/// <summary>
/// 按日期落盘的本地日志。所有拦截/放行/强杀/异常信息写入 <程序目录>\logs\yyyy-MM-dd.log（目录缺失自动创建）。
/// 线程安全；写入失败时静默降级到 Trace，绝不因日志故障影响主流程。
/// </summary>
public sealed class AppLogger : IDisposable
{
    private readonly string _logDir;
    private readonly object _gate = new();
    private readonly bool _alsoWriteTrace;

    public AppLogger(string? logDirectory = null, bool alsoWriteTrace = false)
    {
        // 默认写到“程序当前所在目录下的 logs 文件夹”；不存在则自动创建。
        _logDir = logDirectory
            ?? Path.Combine(AppContext.BaseDirectory, "logs");
        _alsoWriteTrace = alsoWriteTrace;

        try { Directory.CreateDirectory(_logDir); } catch { }
    }

    public string LogDirectory => _logDir;

    private string CurrentFile => Path.Combine(_logDir, $"{DateTime.Now:yyyy-MM-dd}.log");

    public void Info(string message) => Write("INFO ", message);
    public void Warn(string message) => Write("WARN ", message);
    public void Error(string message, Exception? ex = null)
        => Write("ERROR", ex is null ? message : $"{message} :: {ex.GetType().Name}: {ex.Message}");
    public void Traffic(string message) => Write("TRAF ", message);

    private void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";

        if (_alsoWriteTrace)
            Trace.WriteLine(line);

        lock (_gate)
        {
            try
            {
                // 每次按当前日期取文件，跨天自动切换
                File.AppendAllText(CurrentFile, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // 日志失败不影响主流程
            }
        }
    }

    public void Dispose() { }
}

