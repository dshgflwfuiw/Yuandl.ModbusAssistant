using System.IO;
using System.Text;

namespace Yuandl.ModbusAssistant.Services;

public static class AppLogger
{
    private static readonly object LogLock = new();
    private const int RetainedDays = 14;

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Yuandl.ModbusAssistant",
        "logs");

    static AppLogger()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            CleanupOldLogs();
        }
        catch { /* ignore init IO errors */ }
    }

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message) => Write("WARN", message, null);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);
    public static void Fatal(string message, Exception? ex = null) => Write("FATAL", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            var now = DateTime.Now;
            string filePath = Path.Combine(LogDirectory, $"app-{now:yyyyMMdd}.log");
            var sb = new StringBuilder();
            sb.Append('[').Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("] ");
            sb.Append('[').Append(level).Append("] ");
            sb.AppendLine(message);
            if (ex != null)
                sb.AppendLine(ex.ToString());

            lock (LogLock)
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(filePath, sb.ToString(), Encoding.UTF8);
            }
        }
        catch { /* logging must never crash the host app */ }
    }

    private static void CleanupOldLogs()
    {
        try
        {
            var cutoff = DateTime.Now.Date.AddDays(-RetainedDays);
            foreach (var file in Directory.GetFiles(LogDirectory, "app-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                    File.Delete(file);
            }
        }
        catch { /* ignore cleanup errors */ }
    }
}
