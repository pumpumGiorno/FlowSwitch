using System.Numerics;
using FlowSwitch.Core.Diagnostics;
using FlowSwitch.Core.Scene;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using AlphaMode = Vortice.DCommon.AlphaMode;

namespace FlowSwitch.Rendering;

/// <summary>
/// Rasterises text with DirectWrite (Segoe UI Variable, grayscale antialiasing for transparent
/// targets) into mip-mapped textures, cached by <see cref="TextSpec"/> with LRU eviction.
/// </summary>
internal sealed class TextCache : IDisposable
{
    private const int ScratchWidth = 2048;
    private const int ScratchHeight = 256;
    private const int MaxEntries = 400;

    private readonly GraphicsDevice _gfx;
    private readonly Dictionary<TextSpec, Entry> _entries = new();
    private readonly Dictionary<(FontFamilyKind, int, float), IDWriteTextFormat> _formats = new();
    private readonly ID2D1SolidColorBrush _white;
    private readonly ID3D11Texture2D _scratch;
    private readonly ID2D1Bitmap1 _scratchBitmap;
    private readonly string _display;
    private readonly string _text;
    private readonly string _icons;
    private long _clock;

    private sealed class Entry
    {
        public required GpuTexture Texture;
        public long LastUsed;
    }

    public TextCache(GraphicsDevice gfx)
    {
        _gfx = gfx;
        _white = gfx.D2DContext.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 1f), null);
        gfx.D2DContext.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;

        var desc = new Texture2DDescription
        {
            Width = ScratchWidth,
            Height = ScratchHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        };
        _scratch = gfx.Device.CreateTexture2D(in desc);
        using var surface = _scratch.QueryInterface<IDXGISurface>();
        _scratchBitmap = gfx.D2DContext.CreateBitmapFromDxgiSurface(surface,
            new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), 96f, 96f,
                BitmapOptions.Target | BitmapOptions.CannotDraw));

        using var fonts = gfx.DWrite.GetSystemFontCollection(false);
        _display = Pick(fonts, "Segoe UI Variable Display", "Segoe UI");
        _text = Pick(fonts, "Segoe UI Variable Text", "Segoe UI");
        _icons = Pick(fonts, "Segoe Fluent Icons", "Segoe MDL2 Assets");
    }

    private static string Pick(IDWriteFontCollection fonts, string preferred, string fallback) =>
        fonts.FindFamilyName(preferred, out _) ? preferred : fallback;

    public GpuTexture? Get(TextSpec spec)
    {
        if (_entries.TryGetValue(spec, out var entry))
        {
            entry.LastUsed = ++_clock;
            return entry.Texture;
        }
        GpuTexture? texture;
        try
        {
            texture = Rasterize(spec);
        }
        catch (Exception ex)
        {
            Log.Debug($"Text rasterisation failed: {ex.Message}");
            return null;
        }
        if (texture is null) return null;
        if (_entries.Count >= MaxEntries) Evict();
        _entries[spec] = new Entry { Texture = texture, LastUsed = ++_clock };
        return texture;
    }

    private GpuTexture? Rasterize(TextSpec spec)
    {
        var format = GetFormat(spec.Family, spec.Weight, spec.SizePx);
        float maxWidth = Math.Clamp(spec.MaxWidthPx, 8f, ScratchWidth - 8f);
        using var layout = _gfx.DWrite.CreateTextLayout(spec.Text, format, maxWidth, ScratchHeight - 4);
        var metrics = layout.Metrics;
        int width = Math.Min(ScratchWidth, (int)MathF.Ceiling(metrics.Width) + 4);
        int height = Math.Min(ScratchHeight, (int)MathF.Ceiling(metrics.Height) + 2);
        if (width <= 4 || height <= 2) return null;

        var ctx = _gfx.D2DContext;
        ctx.Target = _scratchBitmap;
        ctx.BeginDraw();
        ctx.Clear(new Color4(0f, 0f, 0f, 0f));
        ctx.DrawTextLayout(new Vector2(2f - metrics.Left, 1f), layout, _white, DrawTextOptions.EnableColorFont);
        ctx.EndDraw();
        ctx.Target = null;

        var texture = GpuTexture.CreateRenderable(_gfx.Device, width, height, mips: true);
        _gfx.Context.CopySubresourceRegion(texture.Texture, 0, 0, 0, 0, _scratch, 0, new Box(0, 0, 0, width, height, 1));
        _gfx.Context.GenerateMips(texture.Srv);
        return texture;
    }

    private IDWriteTextFormat GetFormat(FontFamilyKind family, int weight, float size)
    {
        var key = (family, weight, size);
        if (_formats.TryGetValue(key, out var format)) return format;
        string name = family switch
        {
            FontFamilyKind.Display => _display,
            FontFamilyKind.Icons => _icons,
            _ => _text,
        };
        var fontWeight = weight switch
        {
            >= 700 => FontWeight.Bold,
            >= 600 => FontWeight.SemiBold,
            >= 500 => FontWeight.Medium,
            <= 300 => FontWeight.Light,
            _ => FontWeight.Normal,
        };
        format = _gfx.DWrite.CreateTextFormat(name, null, fontWeight, FontStyle.Normal, FontStretch.Normal, size, "en-us");
        format.WordWrapping = WordWrapping.NoWrap;
        using (var ellipsis = _gfx.DWrite.CreateEllipsisTrimmingSign(format))
            format.SetTrimming(new Trimming { Granularity = TrimmingGranularity.Character }, ellipsis);
        _formats[key] = format;
        return format;
    }

    private void Evict()
    {
        foreach (var (spec, entry) in _entries.OrderBy(e => e.Value.LastUsed).Take(_entries.Count / 4).ToList())
        {
            entry.Texture.Dispose();
            _entries.Remove(spec);
        }
    }

    /// <summary>Frees everything that was not used in the last session.</summary>
    public void Trim(long olderThanClock)
    {
        foreach (var (spec, entry) in _entries.Where(e => e.Value.LastUsed < olderThanClock).ToList())
        {
            entry.Texture.Dispose();
            _entries.Remove(spec);
        }
    }

    public long Clock => _clock;

    public void Dispose()
    {
        foreach (var e in _entries.Values) e.Texture.Dispose();
        _entries.Clear();
        foreach (var f in _formats.Values) f.Dispose();
        _formats.Clear();
        _scratchBitmap.Dispose();
        _scratch.Dispose();
        _white.Dispose();
    }
}
