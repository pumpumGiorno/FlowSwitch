using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace FlowSwitch.Rendering;

/// <summary>
/// The blurred desktop behind the overlay, stored as a mip chain in which every level is a
/// progressively blurrier 2× reduction (13-tap filter). Sampling it at a fractional LOD gives a
/// continuous, animatable blur radius for the price of a few texture reads per pixel.
/// Level 0 is half the monitor resolution (the shaders rely on this).
/// </summary>
internal sealed class BackdropPyramid : IDisposable
{
    private readonly ID3D11Texture2D _texture;
    private readonly ID3D11RenderTargetView[] _levelRtv;
    private readonly ID3D11ShaderResourceView[] _levelSrv;

    public BackdropPyramid(ID3D11Device device, int viewportWidth, int viewportHeight, int levels)
    {
        Width = Math.Max(1, viewportWidth / 2);
        Height = Math.Max(1, viewportHeight / 2);
        Levels = Math.Clamp(levels, 1, GpuTexture.FullMipCount(Width, Height));
        ViewportWidth = viewportWidth;
        ViewportHeight = viewportHeight;

        var desc = new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = (uint)Levels,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
        };
        _texture = device.CreateTexture2D(in desc);
        Srv = device.CreateShaderResourceView(_texture,
            new ShaderResourceViewDescription(_texture, ShaderResourceViewDimension.Texture2D, Format.B8G8R8A8_UNorm, 0, (uint)Levels, 0, 1));
        _levelRtv = new ID3D11RenderTargetView[Levels];
        _levelSrv = new ID3D11ShaderResourceView[Levels];
        for (int i = 0; i < Levels; i++)
        {
            _levelRtv[i] = device.CreateRenderTargetView(_texture,
                new RenderTargetViewDescription(_texture, RenderTargetViewDimension.Texture2D, Format.B8G8R8A8_UNorm, (uint)i, 0, 1));
            _levelSrv[i] = device.CreateShaderResourceView(_texture,
                new ShaderResourceViewDescription(_texture, ShaderResourceViewDimension.Texture2D, Format.B8G8R8A8_UNorm, (uint)i, 1, 0, 1));
        }
    }

    public int Width { get; }
    public int Height { get; }
    public int Levels { get; }
    public int ViewportWidth { get; }
    public int ViewportHeight { get; }

    /// <summary>All levels, sampled by the backdrop and glass shaders.</summary>
    public ID3D11ShaderResourceView Srv { get; }

    /// <summary>True once at least one desktop frame has been processed.</summary>
    public bool IsValid { get; set; }

    public long LastUpdate { get; set; }

    public bool Matches(int viewportWidth, int viewportHeight, int levels) =>
        viewportWidth == ViewportWidth && viewportHeight == ViewportHeight && Math.Clamp(levels, 1, GpuTexture.FullMipCount(Width, Height)) == Levels;

    public (int W, int H) LevelSize(int level) => (Math.Max(1, Width >> level), Math.Max(1, Height >> level));

    public ID3D11RenderTargetView LevelTarget(int level) => _levelRtv[level];

    public ID3D11ShaderResourceView LevelSource(int level) => _levelSrv[level];

    public void Dispose()
    {
        foreach (var v in _levelRtv) v.Dispose();
        foreach (var v in _levelSrv) v.Dispose();
        Srv.Dispose();
        _texture.Dispose();
    }
}
