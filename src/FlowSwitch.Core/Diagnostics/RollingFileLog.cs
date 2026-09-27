using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using FlowSwitch.Core.Settings;

namespace FlowSwitch.Core.Diagnostics;

/// <summary>
/// Asynchronous, bounded, rolling file log (<c>name.log</c>, then <c>name.1.log</c> … when it grows).
/// Writers never block — the keyboard hook and the render thread log too; if the queue is ever
/// full, messages are dropped rather than slowing anything down. <see cref="WriteNow"/> and
/// <see cref="Flush"/> exist for the last words before a crash.
/// </summary>
public sealed class RollingFileLog : ILogSink, IDisposable
{
    private const int MaxQueued = 4096;
    private const long MaxFileBytes = 4 * 1024 * 1024;
    private const int KeepRolled = 4;

    private readonly BlockingCollection<string> _queue = new(MaxQueued);
    private readonly Thread _writer;
    private readonly object _fileGate = new();
    private readonly ManualResetEventSlim _idle = new(true);
    private int _pending;

    /// <summary>Opens (creating the folder) <paramref name="path"/>; falls back to %TEMP% if that folder is not writable.</summary>
    public RollingFileLog(string path)
    {
        FilePath = path;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { }
        }
        catch
        {
            FilePath = Path.Combine(Path.GetTempPath(), "FlowSwitch", "Logs", Path.GetFileName(path));
            try { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); } catch { /* nothing else to try */ }
        }
        _writer = new Thread(WriteLoop) { Name = "FlowSwitch.Log", IsBackground = true, Priority = ThreadPriority.BelowNormal };
        _writer.Start();
    }

    /// <summary>The file actually written (differs from the requested one only after a fallback).</summary>
    public string FilePath { get; }

    public void Write(LogLevel level, string message)
    {
        string line = Format(Tag(level), message);
        if (_queue.IsAddingCompleted) return;
        Interlocked.Increment(ref _pending);
        _idle.Reset();
        if (!_queue.TryAdd(line) && Interlocked.Decrement(ref _pending) == 0) _idle.Set();
    }

    /// <summary>Synchronous write, bypassing the queue (fatal errors, process exit).</summary>
    public void WriteNow(string message)
    {
        try
        {
            lock (_fileGate) File.AppendAllText(FilePath, Format("FTL", message) + Environment.NewLine, new UTF8Encoding(false));
        }
        catch
        {
            // ignored — nothing left to report to
        }
    }

    /// <summary>Waits until everything queued so far is on disk.</summary>
    public void Flush(TimeSpan timeout) => _idle.Wait(timeout);

    private static string Format(string tag, string message)
    {
        var thread = Thread.CurrentThread;
        string name = thread.Name ?? $"T{Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture)}";
        return $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} [{tag}] [{name}] {message}";
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
        try
        {
            foreach (string line in _queue.GetConsumingEnumerable())
            {
                try
                {
                    lock (_fileGate)
                    {
                        if (writer is null || writer.BaseStream.Length > MaxFileBytes)
                        {
                            writer?.Dispose();
                            RollIfNeeded();
                            writer = new StreamWriter(new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete),
                                new UTF8Encoding(false)) { AutoFlush = true };
                        }
                        writer.WriteLine(line);
                        // WriteNow appends through its own handle: keep ours at the end of the file.
                        writer.BaseStream.Seek(0, SeekOrigin.End);
                    }
                }
                catch
                {
                    writer?.Dispose();
                    writer = null;
                }
                finally
                {
                    if (Interlocked.Decrement(ref _pending) <= 0) _idle.Set();
                }
            }
        }
        finally
        {
            writer?.Dispose();
        }
    }

    private void RollIfNeeded()
    {
        try
        {
            if (!File.Exists(FilePath) || new FileInfo(FilePath).Length <= MaxFileBytes) return;
            string stem = Path.Combine(Path.GetDirectoryName(FilePath)!, Path.GetFileNameWithoutExtension(FilePath));
            string ext = Path.GetExtension(FilePath);
            File.Delete($"{stem}.{KeepRolled}{ext}");
            for (int i = KeepRolled - 1; i >= 1; i--)
            {
                if (File.Exists($"{stem}.{i}{ext}")) File.Move($"{stem}.{i}{ext}", $"{stem}.{i + 1}{ext}", overwrite: true);
            }
            File.Move(FilePath, $"{stem}.1{ext}", overwrite: true);
        }
        catch
        {
            // Keep appending to the big file rather than lose messages.
        }
    }

    public void Dispose()
    {
        Flush(TimeSpan.FromSeconds(1));
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(1));
    }
}
