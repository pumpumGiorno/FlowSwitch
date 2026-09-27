using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using FlowSwitch.Core.Diagnostics;
using FlowSwitch.Core.Settings;

namespace FlowSwitch.Diagnostics;

/// <summary>
/// Asynchronous, bounded, rolling file log. Writers never block (the keyboard hook and render
/// thread log too); if the queue is full, messages are dropped rather than slowing anything down.
/// </summary>
internal sealed class FileLogSink : ILogSink, IDisposable
{
    private const int MaxQueued = 2048;
    private const long MaxFileBytes = 4 * 1024 * 1024;
    private const int KeepFiles = 7;

    private readonly BlockingCollection<string> _queue = new(MaxQueued);
    private readonly Thread _writer;
    private readonly string _directory;

    public FileLogSink(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        Prune();
        _writer = new Thread(WriteLoop) { Name = "FlowSwitch.Log", IsBackground = true, Priority = ThreadPriority.BelowNormal };
        _writer.Start();
    }

    public string CurrentFile => Path.Combine(_directory, $"flowswitch-{DateTime.Now:yyyyMMdd}.log");

    public void Write(LogLevel level, string message)
    {
        string line = $"{DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} [{Tag(level)}] " +
                      $"[{Thread.CurrentThread.Name ?? Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture)}] {message}";
        _queue.TryAdd(line);
    }

    /// <summary>Synchronous write for last words before a crash.</summary>
    public void WriteNow(string message)
    {
        try
        {
            File.AppendAllText(CurrentFile, $"{DateTime.Now:HH:mm:ss.fff} [FATAL] {message}{Environment.NewLine}");
        }
        catch
        {
            // ignored
        }
    }

    private static string Tag(LogLevel level) => level switch
    {
        LogLevel.Debug => "DBG",
        LogLevel.Info => "INF",
        LogLevel.Warning => "WRN",
        _ => "ERR",
    };

    private void WriteLoop()
    {
        StreamWriter? writer = null;
        string? path = null;
        try
        {
            foreach (string line in _queue.GetConsumingEnumerable())
            {
                try
                {
                    string target = CurrentFile;
                    if (writer is null || path != target || writer.BaseStream.Length > MaxFileBytes)
                    {
                        writer?.Dispose();
                        path = target;
                        if (File.Exists(path) && new FileInfo(path).Length > MaxFileBytes)
                            File.Move(path, path + ".1", overwrite: true);
                        writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
                        {
                            AutoFlush = true,
                        };
                    }
                    writer.WriteLine(line);
                }
                catch
                {
                    writer?.Dispose();
                    writer = null;
                }
            }
        }
        finally
        {
            writer?.Dispose();
        }
    }

    private void Prune()
    {
        try
        {
            foreach (var file in new DirectoryInfo(_directory).GetFiles("flowswitch-*.log*")
                         .OrderByDescending(f => f.LastWriteTimeUtc).Skip(KeepFiles))
                file.Delete();
        }
        catch
        {
            // ignored
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(1));
    }
}
