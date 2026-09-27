# Architecture

This document explains how FlowSwitch replaces the Alt + Tab experience, why it is built the way
it is, and how it stays safe. Code references are to `src/`.

## 1. What Windows allows

There is no supported API to replace the Alt + Tab switcher. The options, and why FlowSwitch
uses the one it does:

| Approach | Verdict |
|---|---|
| `RegisterHotKey(MOD_ALT, VK_TAB)` | Rejected by Windows: Alt + Tab is reserved by the shell. |
| Replacing or patching the shell's switcher (`explorer.exe` hooks, DLL injection) | Fragile across Windows updates, needs injection into Explorer, and a bug takes the shell down. Not acceptable for something people press hundreds of times a day. |
| The classic-switcher registry value (`AltTabSettings`) | Only toggles between two built-in switchers. |
| **Low-level keyboard hook (`WH_KEYBOARD_LL`)** | Sees Alt + Tab before the shell does, can swallow the Tab, and is removed by Windows automatically when the process exits. Chosen. |

The low-level hook has hard constraints, and the design is shaped around them:

- **Timeout.** Windows calls the hook synchronously for every key. If a hook takes longer than
  `LowLevelHooksTimeout` (≈1 s, silently lower on some systems), Windows skips it — and on
  Windows 7+ may remove it without notice. → The hook thread does nothing but classify keys and post
  messages; it never waits for the UI. The hook is also re-installed every 60 s while idle and after
  resume/unlock, in case Windows dropped it.
- **UIPI.** A normal-integrity process does not receive keystrokes while an elevated window is in the
  foreground. → FlowSwitch simply isn't involved then and Windows' switcher appears. The optional
  *Run with administrator rights* setting starts FlowSwitch from a scheduled task with the highest
  run level so it also covers elevated windows. (`uiAccess` would be the alternative, but it
  requires a signed binary installed under Program Files.)
- **Secure desktop.** UAC prompts, Ctrl + Alt + Del and the lock screen run on another desktop; no
  hook sees them. FlowSwitch detects a missed Alt key-up there and finishes cleanly.
- **Alt must stay real.** Swallowing Alt would desynchronise Windows' own key state. FlowSwitch only
  swallows Tab (and keys typed while the switcher is open). The side effect — the foreground app
  seeing a lone Alt press and opening its menu bar — is neutralised by injecting an unassigned
  virtual key (0xFF) while Alt is held.
- **Foreground lock.** `SetForegroundWindow` is refused unless the caller "owns" the last input.
  The user just pressed Alt, which normally satisfies Windows; the fallbacks are a synthetic
  Alt + unassigned-key tap, a guarded `AttachThreadInput`, and `SwitchToThisWindow`
  (`WindowManagement/WindowActivator.cs`).

The result: FlowSwitch takes over the *visual* Alt + Tab, while window order (MRU), activation and
focus remain Windows' own.

## 2. Processes and threads

```
FlowSwitch.exe (resident, tray)                         FlowSwitch.Settings.exe (on demand)
├─ Main thread        tray icon, IPC, power events       WPF UI, onboarding
├─ Hook thread        WH_KEYBOARD_LL, highest priority   └─ writes settings.json (atomic)
├─ Switcher thread    render loop, window tracking,           posts commands to the host window
│                     session logic, input (MTA)
├─ Watchdog thread    watches the switcher thread
├─ Icon thread (STA)  shell icons + accent colors
└─ Capture worker     creates/destroys WGC sessions
```

The resident process never loads a UI framework: it is Win32 + Direct3D only, starts in a few
milliseconds and idles at zero CPU and GPU. Settings is a separate WPF process started only when
needed.

**Communication.** The hook and the switcher thread share a lock-free `HookBridge`
(`Input/HookBridge.cs`): session id, state, acknowledgement and a render heartbeat, all updated
with interlocked operations. Everything else is posted window messages. The Settings app finds the
hidden host window (`FlowSwitch.Host`) and posts a registered message (`FlowSwitch.Command`) —
open settings, show preview, reload, restart, exit. Settings themselves travel through
`%APPDATA%\FlowSwitch\settings.json`, which both processes watch.

## 3. One Alt + Tab, step by step

1. **Alt down.** The hook posts *Prewarm* (unless disabled): the switcher thread creates graphics
   if needed and starts capturing the active monitor and the four most recent windows, so the blur
   and previews are ready if Tab follows. Prewarm stops by itself after 4 s.
2. **Tab down** (Alt held, no Win key, not passthrough). The hook swallows Tab, starts a session,
   posts *Begin*, injects the unassigned key, and arms a timer of `OverlayTimeoutMs` (150 ms).
3. **Switcher thread** acknowledges first, then snapshots the window list (MRU), builds the session
   (`Core/Session/SwitcherSession.cs`) and waits for the reveal delay (70 ms).
4. **Quick release** (before the reveal): the target is activated immediately with no overlay, and
   an optional 340 ms trace of light outlines the activated window. This is the "fast Alt + Tab"
   path — nothing is slower than Windows.
5. **Reveal.** The overlay window is shown only after its first frame has been presented (no stale
   content), fades in, and the system unfolds with a staggered reveal. After 450 ms of holding, the
   *expanded* stage adds names, search and the desktop strip.
6. **More keys** are forwarded by the hook as *Key* messages; the session updates, the animator
   follows (`Core/Scene/SwitcherAnimator.cs`).
7. **Alt up** → *Commit*: the session emits `ActivateWindowEffect`, the window is activated, and the
   selected card flies into the real window's rectangle while the overlay fades (190 ms).
   **Esc** → *Cancel*: the system rewinds and fades (160 ms).

If step 3 does not acknowledge in time, the hook replays Tab via `SendInput` (marked with an
injection tag it ignores itself) and Windows' switcher opens on that same press.

## 4. Window tracking

`WindowManagement/WindowTracker.cs` is event-driven (WinEvent hooks for create/destroy/show/hide/
name/foreground/minimize/cloak), not polled. It applies the Alt + Tab eligibility rules: visible,
not a tool window unless `WS_EX_APPWINDOW`, not owned by another visible window, not cloaked by the
app itself, not part of the shell. Windows cloaked by the shell are on another virtual desktop
(included on request). The MRU order comes from real foreground changes; windows not activated since
FlowSwitch started keep their Z-order position — which is Windows' own MRU approximation.

App identity (`AppIdentityResolver`) prefers the AppUserModelID (so UWP/packaged apps and grouped
taskbar identities match Windows), then the executable. Icons come from the shell at 256 px on an
STA thread; the accent color is taken from a known-app table (Discord, Spotify, Telegram, Chrome,
Steam, VS Code, Photoshop, …) or extracted from the icon's dominant hue, then normalised in OKLab
so every glow has similar perceived brightness.

## 5. Rendering

- **Surface.** A `WS_EX_NOREDIRECTIONBITMAP | TOPMOST | TOOLWINDOW | NOACTIVATE` window per monitor
  hosts a DirectComposition visual with a flip-model swap chain (premultiplied alpha, frame-latency
  waitable object, max latency 1). The overlay never takes focus, so the app beneath doesn't see
  activation changes, and it is excluded from screen capture (`WDA_EXCLUDEFROMCAPTURE`).
- **Frame pacing.** The switcher thread waits on the swap chain's waitable object and presents with
  vsync, so it runs at the display's refresh rate (60–240 Hz) with one frame of latency. When the
  overlay is closed the thread blocks in `MsgWaitForMultipleObjectsEx(INFINITE)` — no timers, no
  polling, no GPU work. After closing, D3D state is cleared and `IDXGIDevice3::Trim` hands scratch
  memory back to the driver.
- **Backdrop.** The monitor capture is downsampled into a blur pyramid (13-tap filter); the shader
  samples it at fractional LODs, so the blur radius itself can animate smoothly. Cards sample the
  same pyramid for frosted glass.
- **Scene.** `Core/Scene/SceneComposer.cs` turns the animated poses into a display list of
  instanced quads (`DrawList`, 12 float4 parameters each). One HLSL file
  (`src/FlowSwitch/Shaders/FlowSwitch.hlsl`) draws everything with signed distance fields: backdrop,
  cards (rounded glass, edge light, light from below, depth-of-field blur, reflection), glows,
  orbit lines, pills, sprites. Perspective is a real projection (per-quad yaw/pitch/depth). Output
  is dithered to avoid banding in the dark gradients.
- **Text.** DirectWrite (Segoe UI Variable Display/Text) rasterised through Direct2D into cached,
  mip-mapped textures, before the frame's 3D pass begins.

The Core project contains no Windows code; the same display lists are rendered headlessly by the
design preview (see [DESIGN.md](DESIGN.md)).

## 6. Live previews

`Capture/*` uses Windows.Graphics.Capture through raw COM interop (no WinRT UI dependencies):

- Sessions are created on a worker (a few ms each, never on a frame) and only while the overlay is
  open or prewarming; all stop when it closes.
- Frame pools are free-threaded and **pulled** on demand, which is how refresh tiers are enforced
  without extra copies: the selected card every frame, neighbours at ~30 fps, far cards at 8–15 fps
  or a single still (by quality preset), with a max preview size per preset.
- The yellow capture border and the cursor are disabled where Windows allows it.
- Minimised windows, windows on other desktops and failed captures fall back to an icon card on a
  soft accent gradient; the switch is cross-faded when a live frame arrives. Hung windows get a
  "Not responding" badge.

Adaptive quality measures frame times during a session and, if frames exceed the budget, steps
down for the rest of it: half the particles, slower backdrop and far-preview refresh, no motion
trails, and no frosted glass below the High preset.

## 7. Fail-safe

| Failure | What happens |
|---|---|
| Renderer / overlay not (yet) working | `HookBridge.RendererReady` is false: the hook does not touch Alt + Tab at all. A failed renderer is retried after 5 s, 15 s, 1 min, then every 5 min. |
| Session cannot start (no windows, exception, renderer lost) | The switcher *rejects* it and the hook replays that Alt + Tab to Windows immediately. |
| DirectComposition unavailable | The overlay uses an opaque window swap chain instead. |
| Windows.Graphics.Capture / WinRT projection unavailable | No live previews or blurred backdrop; the switcher works with icons. |
| Overlay slow to respond (> 150 ms) | That Alt + Tab is replayed to Windows' switcher. |
| Render loop stalls mid-session (> 0.7 s) | Hook releases the keyboard; if Alt is still held, the native switcher opens. |
| Overlay frozen on screen (> 2.5 s) | Watchdog terminates the process (hook and overlay vanish instantly) and it relaunches. |
| Idle UI thread unresponsive (3 probes) | Clean restart. |
| Crash | Windows removes the hook; `RegisterApplicationRestart` + self-relaunch bring FlowSwitch back. |
| 3 crashes in 10 minutes | Starts in safe mode: hook off, tray balloon explains, one click re-enables. |
| Hook silently removed by Windows | Periodic and post-resume re-install. |
| Missed Alt key-up (secure desktop) | Detected by the 100 ms health timer; the session commits. |
| GPU device lost / frames keep failing | Session closes and is handed to Windows; the renderer is taken out of service (Alt + Tab native) and recreated. |
| Fullscreen game / listed app in front | Native Alt + Tab. |
| Corrupt settings file | Moved aside as `settings.json.corrupt`, defaults used. |

A single-instance mutex prevents two hooks from competing.

## 8. Settings

`Core/Settings` defines the configuration (`FlowSwitchSettings`) with defaults chosen to look
finished without touching anything, a clamping `Normalize()`, and source-generated JSON. Writes are
atomic (temp file + replace, retried when another process holds the file). The resident app watches
the file (debounced) and applies changes live; the Settings app writes ~0.3 s after each edit, so
there is no Apply button.

Startup uses the per-user Run key (`"FlowSwitch.exe" --startup`), which Windows' Startup Apps page
can toggle; with *Run with administrator rights* it is replaced by a scheduled task (logon
trigger, highest run level, no battery conditions).
