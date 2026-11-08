using System.Globalization;
using System.Text;

namespace WinWaechter.Core.Logging;

// Simple daily log file. No-op before Init; never throws.
public static class FileLog
{
    static readonly object Lock = new();
    static string? _dir;

    public static void Init(string dir, int retentionDays = 7)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(dir);
                var cutoff = DateTime.Now.AddDays(-retentionDays);
                foreach (var f in Directory.GetFiles(dir, "winwaechter-*.log"))
                {
                    try { if (File.GetLastWriteTime(f) < cutoff) File.Delete(f); } catch { }
                }
                _dir = dir;
            }
        }
        catch { }
    }

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    static void Write(string level, string message, Exception? ex)
    {
        try
        {
            lock (Lock)
            {
                if (_dir is null) return;
                var now = DateTime.Now;
                var sb = new StringBuilder()
                    .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                    .Append(' ').Append(level).Append(' ').Append(message);
                if (ex is not null) sb.Append(" | ").Append(ex);
                sb.AppendLine();
                var file = Path.Combine(_dir, $"winwaechter-{now:yyyyMMdd}.log");
                File.AppendAllText(file, sb.ToString());
            }
        }
        catch { }
    }
}
