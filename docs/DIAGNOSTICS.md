# Diagnostics

How to find out why Alt + Tab does or does not show FlowSwitch on a real Windows machine.

## Where to look

| What | Where |
|---|---|
| Live state | **Settings → Diagnostics** (asks `FlowSwitch.exe` over IPC every 1.5 s; "Not running" means no answer, never a guess) |
| Self-test report | **Run diagnostics**; also written to `%LOCALAPPDATA%\FlowSwitch\Logs\diagnostics.txt` |
| Resident app log | `%LOCALAPPDATA%\FlowSwitch\Logs\flowswitch.log` (rolls over at 4 MB into `flowswitch.1.log` …) |
| Settings app log | `%LOCALAPPDATA%\FlowSwitch\Logs\settings.log` |
| Settings / crash history | `%APPDATA%\FlowSwitch\settings.json`, `state.json` |

The log is created before anything else runs, so even a crash in the first second leaves a trace.
Every failed Win32 call is logged with the function, its return value, `GetLastError` and its
meaning; every failed Direct3D / DXGI / DirectComposition / D3DCompile call with the stage, the
HRESULT and its meaning; exceptions in full (type, message, stack, inner exceptions).

With **Detailed diagnostic logging** (on by default for now) the hook also logs Alt, Tab, Shift and
Esc transitions — only while Alt is involved, and never any other key — and every decision:
`Alt+Tab intercepted → switcher session N requested` or `Alt+Tab passed to Windows: <reason>`.

## Splitting the chain

`Keyboard → Hook → Controller → Overlay → Renderer → Present`

1. **Test overlay** (Settings) or **Ctrl + Alt + F12** (RegisterHotKey — independent of the hook)
   opens the switcher with the real windows for 5 seconds.
   - Works, but Alt + Tab doesn't → the keyboard path (hook, safe mode, disabled/paused, excluded
     app, elevated window in front).
   - Doesn't work → the renderer path; the log names the failing stage and HRESULT.
2. **Run diagnostics** checks each link: host process, IPC, safe mode, keyboard hook, window
   enumeration, D3D11, DirectComposition, shader compilation, overlay HWND (styles, size), a test
   frame presented to the overlay's swap chain, the WinRT projection, one real Graphics Capture
   frame, and finally a synthetic Alt + Tab injected through the real hook (cancelled with Esc).

## When FlowSwitch lets Windows handle Alt + Tab

The hook only takes Alt + Tab while FlowSwitch is enabled, not paused, not in safe mode **and** the
renderer and overlay window are ready. Anything else — including a renderer that is still starting
or has failed — leaves every key untouched, so Windows' own switcher works. A session that cannot
start is rejected and the swallowed Tab is replayed to Windows on the same key press.

## Root causes found on the first real-Windows run (0.9.0)

Both made the renderer fail on every Windows PC while the hook still swallowed Alt + Tab, so the
key press did nothing at all:

1. **Mismatched WinRT projection in the published folder.** The Settings app targeted
   `net10.0-windows10.0.19041.0`, the resident app `…26100.0`. Both were published into one folder
   and Settings, published second, overwrote `Microsoft.Windows.SDK.NET.dll` with the 10.0.19041
   version. `FlowSwitch.exe` needs 10.0.26100, so the first WinRT type (the capture device, created
   inside `GraphicsDevice`) threw `FileLoadException` and the whole renderer was marked failed.
   Fixed by one shared target framework (`FlowSwitchWindowsTargetFramework` in
   `Directory.Build.props`), publish scripts that refuse conflicting files and verify the output,
   capture code isolated so WinRT problems only cost live previews, and a start-up check.
2. **Shaders never compiled with the real d3dcompiler_47.** `Compiler.Compile(string, …)` passes the
   source length in *characters*, but the UTF-8 source had ~700 non-ASCII characters (box-drawing
   lines in comments), so D3DCompile saw a truncated file: `error X3000: syntax error: unexpected
   end of file` at line 470. Fixed by compiling the exact UTF-8 bytes and keeping the HLSL ASCII
   (enforced by a unit test).

Both were reproduced and verified fixed by running the published build under Wine with Microsoft's
own `d3dcompiler_47.dll`: the renderer initialises, and hardware-like (XTest) Alt + Tab, Alt + Shift
+ Tab, Esc, quick taps, Ctrl + Alt + F12, Test overlay and the self-test all run end to end.
