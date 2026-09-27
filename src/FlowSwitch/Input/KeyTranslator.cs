using FlowSwitch.Interop;
using static FlowSwitch.Interop.Win32;

namespace FlowSwitch.Input;

/// <summary>Turns keys captured by the hook into text for type-to-search, using the foreground keyboard layout.</summary>
internal static unsafe class KeyTranslator
{
    /// <summary>ToUnicodeEx flag: do not change the keyboard state (dead keys), Windows 10 1607+.</summary>
    private const uint NoStateChange = 0x4;

    public static string? ToText(uint vk, uint scanCode, KeyFlags flags)
    {
        if (vk is VK_TAB or VK_RETURN or VK_ESCAPE or VK_BACK or VK_DELETE) return null;
        if ((flags & KeyFlags.Ctrl) != 0) return null;

        byte* state = stackalloc byte[256];
        new Span<byte>(state, 256).Clear();
        if ((flags & KeyFlags.Shift) != 0) state[VK_SHIFT] = 0x80;
        if ((flags & KeyFlags.CapsLock) != 0) state[VK_CAPITAL] = 0x01;

        // Use the layout of the app the user is typing "into", so Cyrillic and other layouts work.
        nint foreground = GetForegroundWindow();
        uint thread = foreground != 0 ? GetWindowThreadProcessId(foreground, out _) : 0;
        nint layout = GetKeyboardLayout(thread);

        char* buffer = stackalloc char[8];
        int n = ToUnicodeEx(vk, scanCode, state, buffer, 8, NoStateChange, layout);
        if (n <= 0) return null;
        var text = new string(buffer, 0, n);
        foreach (char c in text)
            if (char.IsControl(c)) return null;
        return text;
    }
}
