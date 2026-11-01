using System.Collections.Concurrent;
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

/// <summary>
/// 写日志。调用方只管把一行丢进队列就走，由后台线程攒成一批落盘。
/// 之前是每条都 <c>File.AppendAllText</c>：打开 / 写入 / 关闭三次系统调用，
/// 机械硬盘上一次要 5～20 ms，而这全发生在 UI 线程上。
/// 后台线程平时状态是等待，不占 CPU；进程退出前调用 <see cref="Shutdown"/> 把它刷完。
/// </summary>
public static class Log
{
    private const long MaxBytes = 1_500_000;
    private const int MaxBatch = 512;

    private static readonly BlockingCollection<string> Pending = new(new ConcurrentQueue<string>());

    /// <summary>写文件用。正常只有后台线程碰它，退出兜底时别的线程也会进来。</summary>
    private static readonly object FileGate = new();

    /// <summary>后台线程已经收工。之后还有调用就退化成同步写，免得丢日志。</summary>
    private static volatile bool _closed;

    private static readonly Thread WriterThread;

    private static FileStream? _stream;
    private static StreamWriter? _writer;
    private static long _bytes;

    static Log()
    {
        WriterThread = new Thread(WriteLoop)
        {
            IsBackground = true,
            Name = "SoundBridge.Log",
            Priority = ThreadPriority.BelowNormal
        };
        WriterThread.Start();
    }

    /// <summary>
    /// 抹掉设备 Id 里的蓝牙地址。Id 形如 <c>...&amp;0&amp;0123456789AB_C00000000#</c>，
    /// 中间那 12 位十六进制就是手机的 MAC。日志是给人贴到 issue 里求助的，
    /// 既帮不上排查，又能定位到具体某台手机，所以只留前 4 位。
    /// </summary>
    public static string MaskId(string? id)
    {
        if (string.IsNullOrEmpty(id)) return "(空)";

        return System.Text.RegularExpressions.Regex.Replace(
            id, "(?<=&)[0-9A-Fa-f]{12}(?=_)",
            m => m.Value[..4] + "********");
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex is null ? message : $"{message} :: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";

        if (_closed)
        {
            Append(new[] { line });
            return;
        }

        try
        {
            Pending.Add(line);
        }
        catch (InvalidOperationException)
        {
            // 队列已经收工（退出途中），直接写，别把这条丢了
            Append(new[] { line });
        }
        catch
        {
            // 日志不允许影响主流程
        }
    }

    private static void WriteLoop()
    {
        var batch = new List<string>(MaxBatch);

        while (true)
        {
            string? first;
            try
            {
                if (!Pending.TryTake(out first, Timeout.Infinite)) return;
            }
            catch
            {
                return;     // 已 CompleteAdding 且队列空了
            }

            batch.Clear();
            batch.Add(first);

            // 顺手把已经在排队的都收走，一次落盘；限个数免得日志风暴时一批写太久
            while (batch.Count < MaxBatch && Pending.TryTake(out var more)) batch.Add(more);

            Append(batch);
        }
    }

    /// <summary>把一批行写进文件。正常只在后台线程调用，退出兜底时可能在别处。</summary>
    private static void Append(IEnumerable<string> lines)
    {
        lock (FileGate)
        {
            try
            {
                if (_writer is null) Open();

                var writer = _writer;
                if (writer is null) return;

                foreach (var line in lines) writer.WriteLine(line);
                writer.Flush();

                _bytes = _stream?.Length ?? _bytes;

                // 攒够大小就换新文件：这时后台线程正好闲着，开销不影响任何人
                if (_bytes > MaxBytes)
                {
                    Close();
                    Open();
                }
            }
            catch
            {
                // 磁盘满、文件被独占之类，一律不往上抛；关掉句柄等下一次重开
                Close();
            }
        }
    }

    private static void Open()
    {
        try
        {
            var path = AppPaths.LogFile;
            Rotate(path);

            var stream = new FileStream(path, FileMode.Append, FileAccess.Write,
                // 允许别的程序（记事本、看图工具）同时读，用户点「日志」时不必等我们退出
                FileShare.ReadWrite | FileShare.Delete, 4096);

            _stream = stream;
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = false };
            _bytes = stream.Length;
        }
        catch
        {
            _stream = null;
            _writer = null;
        }
    }

    private static void Rotate(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= MaxBytes) return;

            var bak = path + ".old";
            File.Delete(bak);
            File.Move(path, bak);
        }
        catch
        {
            // 换不动就继续往老文件里写，总比丢日志好
        }
    }

    private static void Close()
    {
        try { _writer?.Dispose(); } catch { }
        try { _stream?.Dispose(); } catch { }

        _writer = null;
        _stream = null;
    }

    /// <summary>退出前把队列里剩下的写完。App 关闭时调用一次即可。</summary>
    public static void Shutdown()
    {
        if (_closed) return;
        _closed = true;

        try
        {
            Pending.CompleteAdding();
            // 让后台线程把最后一批落完；它退出时文件自然在 Append 里关闭
            WriterThread.Join(2000);
        }
        catch
        {
            // 忽略：下面照样关句柄
        }

        lock (FileGate) Close();
    }
}
