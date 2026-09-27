using FlowSwitch.Core.Diagnostics;
using FlowSwitch.Core.Settings;

namespace FlowSwitch.Services;

/// <summary>
/// Owns the live settings. The Settings app edits settings.json; this service notices (file
/// watcher, debounced) and publishes the new object. Consumers treat instances as immutable.
/// </summary>
internal sealed class SettingsService : IDisposable
{
    private readonly SettingsStore _store;
    private readonly FileSystemWatcher? _watcher;
    private readonly Timer _debounce;
    private volatile FlowSwitchSettings _current;

    public SettingsService(SettingsStore store)
    {
        _store = store;
        _current = store.Load();
        _debounce = new Timer(_ => Reload(), null, Timeout.Infinite, Timeout.Infinite);
        try
        {
            Directory.CreateDirectory(store.Directory);
            _watcher = new FileSystemWatcher(store.Directory, "settings.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, _) => Schedule();
            _watcher.Created += (_, _) => Schedule();
            _watcher.Renamed += (_, _) => Schedule();
        }
        catch (Exception ex)
        {
            Log.Warn($"Settings file watcher unavailable: {ex.Message}");
        }
    }

    public FlowSwitchSettings Current => _current;

    public SettingsStore Store => _store;

    /// <summary>Raised on a thread-pool thread with the new settings.</summary>
    public event Action<FlowSwitchSettings>? Changed;

    public void Update(Action<FlowSwitchSettings> mutate)
    {
        var copy = _current.Clone();
        mutate(copy);
        copy.Normalize();
        _current = copy;
        try
        {
            _store.Save(copy);
        }
        catch (Exception ex)
        {
            Log.Warn($"Saving settings failed: {ex.Message}");
        }
        Changed?.Invoke(copy);
    }

    private void Schedule() => _debounce.Change(150, Timeout.Infinite);

    public void Reload()
    {
        var next = _store.Load();
        if (SettingsSerializer.Serialize(next) == SettingsSerializer.Serialize(_current)) return;
        _current = next;
        Log.Info("Settings reloaded.");
        Changed?.Invoke(next);
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce.Dispose();
    }
}
