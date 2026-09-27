using System.ComponentModel;
using System.IO;
using System.Windows.Threading;
using FlowSwitch.Core.Ipc;
using FlowSwitch.Core.Settings;

namespace FlowSwitch.Settings.Services;

/// <summary>
/// The settings being edited. Pages bind straight to the <see cref="FlowSwitchSettings"/> object;
/// every edit schedules a short debounced save, and the resident FlowSwitch process picks the file
/// up through its own watcher — so changes apply live, with no Apply button.
/// </summary>
public sealed class SettingsModel : INotifyPropertyChanged, IDisposable
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(280);

    private readonly SettingsStore _store;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _reloadTimer;
    private readonly FileSystemWatcher? _watcher;
    private string _lastPersisted;
    private bool _suspended;

    public SettingsModel(SettingsStore store)
    {
        _store = store;
        Settings = store.Load();
        _lastPersisted = SettingsSerializer.Serialize(Settings);

        _saveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = SaveDelay };
        _saveTimer.Tick += (_, _) => SaveNow();
        _reloadTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        _reloadTimer.Tick += (_, _) =>
        {
            _reloadTimer.Stop();
            ReloadFromDisk();
        };

        try
        {
            Directory.CreateDirectory(store.Directory);
            _watcher = new FileSystemWatcher(store.Directory, Path.GetFileName(store.SettingsPath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += OnFileEvent;
            _watcher.Created += OnFileEvent;
            _watcher.Renamed += OnFileEvent;
        }
        catch (Exception)
        {
            // Without a watcher external edits simply are not picked up live.
            _watcher = null;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after every edit (debounced) and after an external reload.</summary>
    public event EventHandler? Changed;

    public FlowSwitchSettings Settings { get; private set; }

    public SettingsStore Store => _store;

    /// <summary>Called by the UI whenever a bound control changed.</summary>
    public void MarkDirty()
    {
        if (_suspended) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SaveNow()
    {
        _saveTimer.Stop();
        Settings.Normalize();
        string json = SettingsSerializer.Serialize(Settings);
        if (json == _lastPersisted) return;
        try
        {
            _store.Save(Settings);
            _lastPersisted = json;
            // The resident process watches the file too; the explicit nudge covers a disabled watcher.
            FlowSwitchHost.Send(IpcProtocol.ReloadSettings);
        }
        catch (IOException)
        {
            // The file is briefly locked by another writer: try again shortly.
            _saveTimer.Start();
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Applies a programmatic change (not from a bound control) and saves it.</summary>
    public void Update(Action<FlowSwitchSettings> change)
    {
        change(Settings);
        Rebind();
        MarkDirty();
    }

    public void ResetToDefaults()
    {
        var fresh = new FlowSwitchSettings();
        // Keep the things the user would not expect a "reset look & feel" to touch.
        fresh.General.OnboardingCompleted = Settings.General.OnboardingCompleted;
        fresh.General.LaunchAtStartup = Settings.General.LaunchAtStartup;
        fresh.Advanced.RunElevated = Settings.Advanced.RunElevated;
        Replace(fresh);
        SaveNow();
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        // FileSystemWatcher fires on a pool thread, often several times per write.
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _reloadTimer.Stop();
            _reloadTimer.Start();
        });
    }

    private void ReloadFromDisk()
    {
        if (_saveTimer.IsEnabled) return; // Our own pending edit wins; it is about to be written.
        FlowSwitchSettings loaded;
        try
        {
            if (!File.Exists(_store.SettingsPath)) return;
            loaded = _store.Load();
        }
        catch (Exception)
        {
            return;
        }
        string json = SettingsSerializer.Serialize(loaded);
        if (json == _lastPersisted) return;
        _lastPersisted = json;
        Replace(loaded);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Replace(FlowSwitchSettings settings)
    {
        Settings = settings;
        Rebind();
    }

    /// <summary>Refreshes every binding (the settings classes are plain objects without change notification).</summary>
    private void Rebind()
    {
        _suspended = true;
        try
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Settings)));
        }
        finally
        {
            // Bindings update synchronously; control events raised by the refresh are not user edits.
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => _suspended = false);
        }
    }

    public void Dispose()
    {
        if (_saveTimer.IsEnabled) SaveNow();
        _watcher?.Dispose();
    }
}
