using System.Text;
using FlowSwitch.Core.Diagnostics;

namespace FlowSwitch.Core.Settings;

/// <summary>
/// Loads and saves settings. Writes are atomic (temp file + replace) so a crash mid-write can
/// never leave a truncated file; a corrupt file is moved aside and defaults are used.
/// </summary>
public sealed class SettingsStore
{
    public SettingsStore(string directory)
    {
        Directory = directory;
        SettingsPath = Path.Combine(directory, "settings.json");
        StatePath = Path.Combine(directory, "state.json");
    }

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlowSwitch");

    public string Directory { get; }
    public string SettingsPath { get; }
    public string StatePath { get; }

    public FlowSwitchSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new FlowSwitchSettings();
            string json = ReadShared(SettingsPath);
            return SettingsSerializer.Deserialize(json);
        }
        catch (Exception ex)
        {
            Log.Warn($"Settings file is unreadable, using defaults: {ex.Message}");
            TryMoveAside(SettingsPath);
            return new FlowSwitchSettings();
        }
    }

    public void Save(FlowSwitchSettings settings) => WriteAtomic(SettingsPath, SettingsSerializer.Serialize(settings));

    public RuntimeState LoadState()
    {
        try
        {
            return File.Exists(StatePath) ? SettingsSerializer.DeserializeState(ReadShared(StatePath)) : new RuntimeState();
        }
        catch (Exception ex)
        {
            Log.Warn($"State file is unreadable: {ex.Message}");
            return new RuntimeState();
        }
    }

    public void SaveState(RuntimeState state) => WriteAtomic(StatePath, SettingsSerializer.SerializeState(state));

    private void WriteAtomic(string path, string content)
    {
        System.IO.Directory.CreateDirectory(Directory);
        string temp = path + ".tmp";
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                // Another process (the settings app / file watcher) may briefly hold the file.
                Thread.Sleep(15 * (attempt + 1));
            }
        }
    }

    private static string ReadShared(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return reader.ReadToEnd();
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(15 * (attempt + 1));
            }
        }
    }

    private static void TryMoveAside(string path)
    {
        try
        {
            if (File.Exists(path)) File.Move(path, path + ".corrupt", overwrite: true);
        }
        catch
        {
            // Best effort only.
        }
    }
}
