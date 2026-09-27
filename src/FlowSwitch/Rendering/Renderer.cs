using System.Diagnostics;
using System.Numerics;
using FlowSwitch.Core.Diagnostics;
using FlowSwitch.Core.Scene;
using FlowSwitch.WindowManagement;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace FlowSwitch.Rendering;

/// <summary>
/// Executes FlowSwitch display lists with Direct3D 11 and owns every GPU resource they refer to:
/// cached window previews, app icons, text and the blurred backdrop.
/// </summary>
internal sealed unsafe class Renderer : ISceneResources, IDisposable
{
    private const int MaxCachedPreviews = 48;

    private readonly GraphicsDevice _gfx;
    private readonly ShaderLibrary _shaders;
    private readonly ID3D11Buffer _frameCB;
    private readonly ID3D11Buffer _drawCB;
    private readonly ID3D11BlendState _premultiplied;
    private readonly ID3D11BlendState _opaque;
    private readonly ID3D11RasterizerState _raster;
    private readonly ID3D11SamplerState _linear;
    private readonly GpuTexture _empty;
    private readonly Dictionary<long, PreviewEntry> _previews = new();
    private readonly Dictionary<string, GpuTexture> _icons = new(StringComparer.OrdinalIgnoreCase);
    private ID3D11Texture2D? _scratch;
    private ID3D11ShaderResourceView? _scratchSrv;
    private BackdropPyramid? _backdrop;

    private sealed class PreviewEntry
    {
        public required GpuTexture Texture;
        public float Aspect;
        public bool Live;
        public long Updated;
    }

    public Renderer(GraphicsDevice gfx, string shaderCache)
    {
        _gfx = gfx;
        var device = gfx.Device;
        _shaders = new ShaderLibrary(device, shaderCache);
        _frameCB = device.CreateBuffer(new BufferDescription(64, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write, ResourceOptionFlags.None, 0));
        _drawCB = device.CreateBuffer(new BufferDescription(192, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write, ResourceOptionFlags.None, 0));
        _premultiplied = device.CreateBlendState(new BlendDescription(Blend.One, Blend.InverseSourceAlpha, Blend.One, Blend.InverseSourceAlpha));
        _opaque = device.CreateBlendState(BlendDescription.Opaque);
        _raster = device.CreateRasterizerState(RasterizerDescription.CullNone);
        _linear = device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp, 0f, 1, ComparisonFunction.Never, 0f, float.MaxValue));
        byte[] transparent = new byte[4];
        _empty = GpuTexture.FromPixels(device, gfx.Context, transparent, 1, 1);
        Text = new TextCache(gfx);
    }

    public TextCache Text { get; }

    public BackdropPyramid? Backdrop => _backdrop;

    // ───────────────────────────────── ISceneResources ─────────────────────────────────

    public bool HasBackdrop => _backdrop?.IsValid == true;

    public PreviewInfo GetPreview(long windowHandle) =>
        _previews.TryGetValue(windowHandle, out var e)
            ? new PreviewInfo(true, new Vector4(0f, 0f, 1f, 1f), e.Live, e.Aspect)
            : PreviewInfo.Missing;

    public bool HasIcon(string appId) => _icons.ContainsKey(appId);

    // ───────────────────────────────── scene ─────────────────────────────────

    public void Render(ID3D11RenderTargetView target, int width, int height, DrawList list, bool useBackdrop)
    {
        var ctx = _gfx.Context;
        ctx.OMSetRenderTargets(target, null);
        ctx.RSSetViewport(0f, 0f, width, height, 0f, 1f);
        ctx.ClearRenderTargetView(target, new Color4(0f, 0f, 0f, 0f));
        ctx.OMSetBlendState(_premultiplied);
        ctx.RSSetState(_raster);
        ctx.IASetInputLayout(null);
        ctx.PSSetSampler(0, _linear);
        ctx.VSSetConstantBuffer(0, _frameCB);
        ctx.PSSetConstantBuffer(0, _frameCB);
        ctx.VSSetConstantBuffer(1, _drawCB);
        ctx.PSSetConstantBuffer(1, _drawCB);

        var frame = list.Frame;
        if (!useBackdrop || !HasBackdrop) frame.Globals.X = 0f;
        Upload(_frameCB, &frame, sizeof(FrameConstants));
        ctx.PSSetShaderResource(0, useBackdrop && HasBackdrop ? _backdrop!.Srv : _empty.Srv);

        ShaderKind? currentShader = null;
        var commands = list.MutableCommands;
        for (int i = 0; i < commands.Length; i++)
        {
            ref var cmd = ref commands[i];
            GpuTexture? texture = Resolve(cmd.Texture);
            if (cmd.Texture.Kind is TextureKind.Icon or TextureKind.Text && texture is null) continue;
            if (TextPlacement.IsText(cmd)) TextPlacement.Resolve(ref cmd, new Vector2(texture!.Width, texture.Height));

            if (currentShader != cmd.Shader)
            {
                currentShader = cmd.Shader;
                bool fullscreen = cmd.Shader == ShaderKind.Backdrop;
                ctx.VSSetShader(fullscreen ? _shaders.FullscreenVS : _shaders.QuadVS);
                ctx.PSSetShader(_shaders.Pixel(cmd.Shader));
                ctx.IASetPrimitiveTopology(fullscreen ? PrimitiveTopology.TriangleList : PrimitiveTopology.TriangleStrip);
            }

            fixed (ParamBlock* p = &cmd.P) Upload(_drawCB, p, sizeof(ParamBlock));
            ctx.PSSetShaderResource(1, (texture ?? _empty).Srv);
            ctx.Draw(cmd.Shader == ShaderKind.Backdrop ? 3u : 4u, 0);
        }

        ctx.PSSetShaderResource(0, null!);
        ctx.PSSetShaderResource(1, null!);
    }

    private GpuTexture? Resolve(TextureRef texture) => texture.Kind switch
    {
        TextureKind.Preview => _previews.TryGetValue(texture.Handle, out var p) ? p.Texture : null,
        TextureKind.Icon => texture.Key is not null && _icons.TryGetValue(texture.Key, out var icon) ? icon : null,
        TextureKind.Text => texture.Text is not null ? Text.Get(texture.Text) : null,
        _ => null,
    };

    private void Upload(ID3D11Buffer buffer, void* data, int size)
    {
        var mapped = _gfx.Context.Map(buffer, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        Buffer.MemoryCopy(data, (void*)mapped.DataPointer, size, size);
        _gfx.Context.Unmap(buffer, 0);
    }

    // ───────────────────────────────── previews ─────────────────────────────────

    /// <summary>Resamples a captured frame into the window's cached, mip-mapped preview texture.</summary>
    public void UpdatePreview(long hwnd, ID3D11Texture2D frame, int contentWidth, int contentHeight, int maxDimension)
    {
        var desc = frame.Description;
        int texW = (int)desc.Width, texH = (int)desc.Height;
        contentWidth = Math.Clamp(contentWidth, 1, texW);
        contentHeight = Math.Clamp(contentHeight, 1, texH);

        float scale = maxDimension <= 0 ? 1f : Math.Min(1f, maxDimension / (float)Math.Max(contentWidth, contentHeight));
        int tw = Math.Max(1, (int)MathF.Round(contentWidth * scale));
        int th = Math.Max(1, (int)MathF.Round(contentHeight * scale));

        if (!_previews.TryGetValue(hwnd, out var entry) || entry.Texture.Width != tw || entry.Texture.Height != th)
        {
            entry?.Texture.Dispose();
            if (entry is null && _previews.Count >= MaxCachedPreviews) EvictOldestPreview();
            entry = new PreviewEntry { Texture = GpuTexture.CreateRenderable(_gfx.Device, tw, th, mips: true) };
            _previews[hwnd] = entry;
        }

        var source = SourceView(frame, desc, contentWidth, contentHeight, out bool ownsView);
        try
        {
            float ratio = contentWidth / (float)tw;
            var p0 = new Vector4(1f / texW, 1f / texH, Math.Clamp(MathF.Ceiling(ratio), 1f, 6f), ratio);
            var p1 = new Vector4(contentWidth / (float)texW, contentHeight / (float)texH, 0f, 0f);
            if (!ownsView) p1 = new Vector4(contentWidth / (float)_scratchWidth, contentHeight / (float)_scratchHeight, 0f, 0f);
            if (!ownsView) p0 = new Vector4(1f / _scratchWidth, 1f / _scratchHeight, p0.Z, p0.W);
            RunPass(_shaders.Resample, source, entry.Texture.Rtv!, tw, th, p0, p1);
        }
        finally
        {
            if (ownsView) source.Dispose();
        }
        _gfx.Context.GenerateMips(entry.Texture.Srv);
        entry.Aspect = contentWidth / (float)contentHeight;
        entry.Live = true;
        entry.Updated = Stopwatch.GetTimestamp();
    }

    /// <summary>Marks cached previews as stale snapshots (the overlay closed; captures stopped).</summary>
    public void MarkPreviewsStale()
    {
        foreach (var e in _previews.Values) e.Live = false;
    }

    public void TrimPreviews(IReadOnlySet<long> alive)
    {
        foreach (var (hwnd, e) in _previews.Where(p => !alive.Contains(p.Key)).ToList())
        {
            e.Texture.Dispose();
            _previews.Remove(hwnd);
        }
    }

    private void EvictOldestPreview()
    {
        var oldest = _previews.OrderBy(p => p.Value.Updated).First();
        oldest.Value.Texture.Dispose();
        _previews.Remove(oldest.Key);
    }

    private int _scratchWidth, _scratchHeight;

    /// <summary>
    /// A shader view of the frame. Capture textures are normally shader-bindable; if a driver
    /// hands out one that isn't, the frame is first copied into a reusable scratch texture.
    /// </summary>
    private ID3D11ShaderResourceView SourceView(ID3D11Texture2D frame, Texture2DDescription desc, int contentW, int contentH, out bool ownsView)
    {
        if ((desc.BindFlags & BindFlags.ShaderResource) != 0)
        {
            try
            {
                ownsView = true;
                return _gfx.Device.CreateShaderResourceView(frame,
                    new ShaderResourceViewDescription(frame, ShaderResourceViewDimension.Texture2D, Format.B8G8R8A8_UNorm, 0, 1, 0, 1));
            }
            catch (Exception ex)
            {
                Log.Debug($"Direct frame view failed, copying instead: {ex.Message}");
            }
        }

        if (_scratch is null || _scratchWidth < contentW || _scratchHeight < contentH)
        {
            _scratchSrv?.Dispose();
            _scratch?.Dispose();
            _scratchWidth = Math.Max(contentW, _scratchWidth);
            _scratchHeight = Math.Max(contentH, _scratchHeight);
            var scratchDesc = new Texture2DDescription
            {
                Width = (uint)_scratchWidth,
                Height = (uint)_scratchHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
            };
            _scratch = _gfx.Device.CreateTexture2D(in scratchDesc);
            _scratchSrv = _gfx.Device.CreateShaderResourceView(_scratch, null);
        }
        _gfx.Context.CopySubresourceRegion(_scratch, 0, 0, 0, 0, frame, 0, new Box(0, 0, 0, contentW, contentH, 1));
        ownsView = false;
        return _scratchSrv!;
    }

    // ───────────────────────────────── backdrop ─────────────────────────────────

    /// <summary>Builds the blur pyramid from a desktop frame.</summary>
    public void UpdateBackdrop(ID3D11Texture2D frame, int contentWidth, int contentHeight, int viewportWidth, int viewportHeight, int levels)
    {
        if (_backdrop is null || !_backdrop.Matches(viewportWidth, viewportHeight, levels))
        {
            _backdrop?.Dispose();
            _backdrop = new BackdropPyramid(_gfx.Device, viewportWidth, viewportHeight, levels);
        }

        var desc = frame.Description;
        int texW = (int)desc.Width, texH = (int)desc.Height;
        contentWidth = Math.Clamp(contentWidth, 1, texW);
        contentHeight = Math.Clamp(contentHeight, 1, texH);
        var source = SourceView(frame, desc, contentWidth, contentHeight, out bool ownsView);
        try
        {
            float sw = ownsView ? texW : _scratchWidth, sh = ownsView ? texH : _scratchHeight;
            var (w0, h0) = _backdrop.LevelSize(0);
            RunPass(_shaders.Downsample, source, _backdrop.LevelTarget(0), w0, h0,
                new Vector4(1f / sw, 1f / sh, 0f, 0f), new Vector4(contentWidth / sw, contentHeight / sh, 0f, 0f));
        }
        finally
        {
            if (ownsView) source.Dispose();
        }

        for (int level = 1; level < _backdrop.Levels; level++)
        {
            var (pw, ph) = _backdrop.LevelSize(level - 1);
            var (w, h) = _backdrop.LevelSize(level);
            RunPass(_shaders.Downsample, _backdrop.LevelSource(level - 1), _backdrop.LevelTarget(level), w, h,
                new Vector4(1f / pw, 1f / ph, 0f, 0f), new Vector4(1f, 1f, 0f, 0f));
        }
        _backdrop.IsValid = true;
        _backdrop.LastUpdate = Stopwatch.GetTimestamp();
    }

    public void InvalidateBackdrop()
    {
        if (_backdrop is not null) _backdrop.IsValid = false;
    }

    private void RunPass(ID3D11PixelShader shader, ID3D11ShaderResourceView source, ID3D11RenderTargetView target, int width, int height, Vector4 p0, Vector4 p1)
    {
        var ctx = _gfx.Context;
        ctx.PSSetShaderResource(1, null!);
        ctx.OMSetRenderTargets(target, null);
        ctx.RSSetViewport(0f, 0f, width, height, 0f, 1f);
        ctx.OMSetBlendState(_opaque);
        ctx.RSSetState(_raster);
        ctx.IASetInputLayout(null);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.VSSetShader(_shaders.FullscreenVS);
        ctx.PSSetShader(shader);
        ctx.PSSetSampler(0, _linear);
        ctx.VSSetConstantBuffer(0, _frameCB);
        ctx.PSSetConstantBuffer(0, _frameCB);
        ctx.VSSetConstantBuffer(1, _drawCB);
        ctx.PSSetConstantBuffer(1, _drawCB);

        ParamBlock block = default;
        block[0] = p0;
        block[1] = p1;
        Upload(_drawCB, &block, sizeof(ParamBlock));
        ctx.PSSetShaderResource(1, source);
        ctx.Draw(3, 0);
        ctx.PSSetShaderResource(1, null!);
        ctx.OMSetRenderTargets((ID3D11RenderTargetView)null!, null);
    }

    // ───────────────────────────────── icons ─────────────────────────────────

    public void UploadIcon(IconResult icon)
    {
        if (_icons.Remove(icon.AppId, out var old)) old.Dispose();
        _icons[icon.AppId] = GpuTexture.FromPixels(_gfx.Device, _gfx.Context, icon.Pixels, icon.Width, icon.Height);
    }

    public void Dispose()
    {
        foreach (var p in _previews.Values) p.Texture.Dispose();
        foreach (var i in _icons.Values) i.Dispose();
        _previews.Clear();
        _icons.Clear();
        _backdrop?.Dispose();
        _scratchSrv?.Dispose();
        _scratch?.Dispose();
        Text.Dispose();
        _empty.Dispose();
        _linear.Dispose();
        _raster.Dispose();
        _opaque.Dispose();
        _premultiplied.Dispose();
        _drawCB.Dispose();
        _frameCB.Dispose();
        _shaders.Dispose();
    }
}
