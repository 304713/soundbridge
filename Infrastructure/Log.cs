using System.IO;
using System.Text;

namespace SoundBridge.Infrastructure;

public static class AppPaths
{
    public static string DataDir { get; } = EnsureDir(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SoundBridge"));

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");

    public static string LogFile => Path.Combine(DataDir, "soundbridge.log");

    private static string EnsureDir(string dir)
    {
        try { Directory.CreateDirectory(dir); } catch { }
        return dir;
    }
}

public static class Log
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex is null ? message : $"{message} :: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                var file = AppPaths.LogFile;
                if (File.Exists(file) && new FileInfo(file).Length > 1_500_000)
                {
                    var bak = file + ".old";
                    File.Delete(bak);
                    File.Move(file, bak);
                }

                File.AppendAllText(file,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // 日志不允许影响主流程
        }
    }
}
