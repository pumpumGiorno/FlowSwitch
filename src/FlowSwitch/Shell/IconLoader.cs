using System.Buffers.Binary;
using System.Reflection;
using static FlowSwitch.Interop.Win32;

namespace FlowSwitch.Shell;

/// <summary>Creates HICONs from the .ico files embedded in the assembly, picking the best size for the current DPI.</summary>
internal static unsafe class IconLoader
{
    public static nint Load(string resourceName, int desiredSize)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (stream is null) return 0;
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return FromIco(bytes, desiredSize);
    }

    public static nint FromIco(ReadOnlySpan<byte> ico, int desiredSize)
    {
        if (ico.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(ico[2..]) != 1) return 0;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(ico[4..]);
        int bestIndex = -1, bestSize = int.MaxValue, fallbackIndex = -1, fallbackSize = 0;
        for (int i = 0; i < count; i++)
        {
            var entry = ico.Slice(6 + i * 16, 16);
            int size = entry[0] == 0 ? 256 : entry[0];
            if (size >= desiredSize && size < bestSize) { bestSize = size; bestIndex = i; }
            if (size > fallbackSize) { fallbackSize = size; fallbackIndex = i; }
        }
        int index = bestIndex >= 0 ? bestIndex : fallbackIndex;
        if (index < 0) return 0;

        var chosen = ico.Slice(6 + index * 16, 16);
        int length = BinaryPrimitives.ReadInt32LittleEndian(chosen[8..]);
        int offset = BinaryPrimitives.ReadInt32LittleEndian(chosen[12..]);
        if (offset < 0 || length <= 0 || offset + length > ico.Length) return 0;
        fixed (byte* p = ico)
        {
            return CreateIconFromResourceEx(p + offset, (uint)length, true, 0x00030000, desiredSize, desiredSize, 0);
        }
    }
}
