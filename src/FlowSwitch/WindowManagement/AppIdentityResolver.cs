using System.Diagnostics;
using System.Runtime.InteropServices;
using FlowSwitch.Core.Model;
using FlowSwitch.Interop;
using static FlowSwitch.Interop.Win32;

namespace FlowSwitch.WindowManagement;

/// <summary>
/// Works out which app a window belongs to and what users call it ("Google Chrome", not
/// "chrome.exe"). Results are cached per executable / package.
/// </summary>
internal sealed unsafe class AppIdentityResolver
{
    private readonly Dictionary<uint, (string? Path, string? Aumid, long Stamp)> _processCache = new();
    private readonly Dictionary<string, AppIdentity> _identities = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _refinedNames = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> FriendlyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["explorer.exe"] = "File Explorer",
        ["msedge.exe"] = "Microsoft Edge",
        ["code.exe"] = "Visual Studio Code",
        ["devenv.exe"] = "Visual Studio",
        ["windowsterminal.exe"] = "Windows Terminal",
        ["wt.exe"] = "Windows Terminal",
        ["cmd.exe"] = "Command Prompt",
        ["powershell.exe"] = "Windows PowerShell",
        ["pwsh.exe"] = "PowerShell",
        ["systemsettings.exe"] = "Settings",
        ["taskmgr.exe"] = "Task Manager",
        ["mspaint.exe"] = "Paint",
        ["notepad.exe"] = "Notepad",
        ["winword.exe"] = "Word",
        ["excel.exe"] = "Excel",
        ["powerpnt.exe"] = "PowerPoint",
        ["outlook.exe"] = "Outlook",
        ["olk.exe"] = "Outlook",
        ["ms-teams.exe"] = "Microsoft Teams",
        ["mstsc.exe"] = "Remote Desktop",
        ["steamwebhelper.exe"] = "Steam",
        ["applicationframehost.exe"] = "App",
    };

    /// <summary>Called when a better display name arrives from the shell (Store apps).</summary>
    public void RefineDisplayName(string appId, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        _refinedNames[appId] = name;
        if (_identities.TryGetValue(appId, out var existing) && existing.DisplayName != name)
            _identities[appId] = existing with { DisplayName = name };
    }

    public AppIdentity Resolve(nint hwnd, uint pid, string className, string title)
    {
        // Store apps live inside ApplicationFrameHost; the real process owns the CoreWindow child.
        if (className == "ApplicationFrameWindow")
        {
            nint core = FindCoreWindow(hwnd);
            if (core != 0)
            {
                GetWindowThreadProcessId(core, out uint corePid);
                if (corePid != 0) pid = corePid;
            }
        }

        var (path, aumid) = GetProcessInfo(pid);
        string exeName = path is null ? className : Path.GetFileName(path);
        string id = aumid ?? path?.ToLowerInvariant() ?? $"class:{className}";

        if (_identities.TryGetValue(id, out var cached)) return cached;

        string display = _refinedNames.TryGetValue(id, out var refined) ? refined : FriendlyName(path, exeName, title, aumid);
        var identity = new AppIdentity(id, display, exeName, path, aumid);
        _identities[id] = identity;
        return identity;
    }

    private static string FriendlyName(string? path, string exeName, string title, string? aumid)
    {
        if (FriendlyNames.TryGetValue(exeName, out var known) && known != "App") return known;
        if (path is not null)
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                string? name = Clean(info.FileDescription);
                if (string.IsNullOrEmpty(name) || name.Length > 48 || name.Contains(".exe", StringComparison.OrdinalIgnoreCase))
                    name = Clean(info.ProductName);
                if (!string.IsNullOrEmpty(name) && name.Length <= 48) return name;
            }
            catch
            {
                // Protected / inaccessible binaries: fall through.
            }
        }
        if (aumid is not null && !string.IsNullOrWhiteSpace(title)) return title;
        string stem = Path.GetFileNameWithoutExtension(exeName);
        return stem.Length > 0 ? char.ToUpperInvariant(stem[0]) + stem[1..] : "Application";
    }

    private static string? Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        // "Microsoft® Windows® Operating System" style noise.
        s = s.Replace("®", string.Empty).Replace("™", string.Empty).Trim();
        return s;
    }

    private (string? Path, string? Aumid) GetProcessInfo(uint pid)
    {
        long now = Environment.TickCount64;
        if (_processCache.TryGetValue(pid, out var entry) && now - entry.Stamp < 60_000) return (entry.Path, entry.Aumid);

        string? path = null, aumid = null;
        nint process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process != 0)
        {
            try
            {
                char* buffer = stackalloc char[1024];
                uint size = 1024;
                if (QueryFullProcessImageNameW(process, 0, buffer, ref size)) path = new string(buffer, 0, (int)size);

                if (s_aumidAvailable)
                {
                    uint len = 256;
                    char* idBuffer = stackalloc char[256];
                    try
                    {
                        if (GetApplicationUserModelId(process, ref len, idBuffer) == 0 && len > 1)
                            aumid = new string(idBuffer, 0, (int)len - 1);
                    }
                    catch (EntryPointNotFoundException)
                    {
                        s_aumidAvailable = false; // not on this system; packaged-app identity is optional
                    }
                }
            }
            finally
            {
                CloseHandle(process);
            }
        }
        _processCache[pid] = (path, aumid, now);
        return (path, aumid);
    }

    private static bool s_aumidAvailable = true;

    /// <summary>Drops cached process entries that are no longer referenced.</summary>
    public void Trim(IEnumerable<uint> livePids)
    {
        var live = new HashSet<uint>(livePids);
        foreach (uint pid in _processCache.Keys.ToList())
            if (!live.Contains(pid)) _processCache.Remove(pid);
    }

    [ThreadStatic] private static nint t_foundCore;

    private static nint FindCoreWindow(nint frame)
    {
        t_foundCore = 0;
        EnumChildWindows(frame, &FindCoreCallback, 0);
        return t_foundCore;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int FindCoreCallback(nint hwnd, nint lParam)
    {
        if (GetClassName(hwnd) == "Windows.UI.Core.CoreWindow")
        {
            t_foundCore = hwnd;
            return 0;
        }
        return 1;
    }
}
