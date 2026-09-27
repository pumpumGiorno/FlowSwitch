using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace FlowSwitch.Rendering;

/// <summary>A 2D BGRA texture with a shader view, optional render-target view(s) and optional mip chain.</summary>
internal sealed class GpuTexture : IDisposable
{
    private GpuTexture(ID3D11Texture2D texture, ID3D11ShaderResourceView srv, ID3D11RenderTargetView? rtv, int width, int height, int mips)
    {
        Texture = texture;
        Srv = srv;
        Rtv = rtv;
        Width = width;
        Height = height;
        MipLevels = mips;
    }

    public ID3D11Texture2D Texture { get; }
    public ID3D11ShaderResourceView Srv { get; }
    /// <summary>Render target view of mip 0 (null for immutable textures).</summary>
    public ID3D11RenderTargetView? Rtv { get; }
    public int Width { get; }
    public int Height { get; }
    public int MipLevels { get; }
    public long Bytes => (long)Width * Height * 4 * (MipLevels > 1 ? 4 : 3) / 3;

    public static int FullMipCount(int width, int height) => 1 + (int)Math.Floor(Math.Log2(Math.Max(1, Math.Max(width, height))));

    /// <summary>A render-target texture with an automatically generated mip chain.</summary>
    public static GpuTexture CreateRenderable(ID3D11Device device, int width, int height, bool mips)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        int levels = mips ? FullMipCount(width, height) : 1;
        var desc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = (uint)levels,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            MiscFlags = mips ? ResourceOptionFlags.GenerateMips : ResourceOptionFlags.None,
        };
        var texture = device.CreateTexture2D(in desc);
        var srv = device.CreateShaderResourceView(texture,
            new ShaderResourceViewDescription(texture, ShaderResourceViewDimension.Texture2D, Format.B8G8R8A8_UNorm, 0, (uint)levels, 0, 1));
        var rtv = device.CreateRenderTargetView(texture,
            new RenderTargetViewDescription(texture, RenderTargetViewDimension.Texture2D, Format.B8G8R8A8_UNorm, 0, 0, 1));
        return new GpuTexture(texture, srv, rtv, width, height, levels);
    }

    /// <summary>Uploads premultiplied BGRA pixels and builds mips.</summary>
    public static unsafe GpuTexture FromPixels(ID3D11Device device, ID3D11DeviceContext context, ReadOnlySpan<byte> bgra, int width, int height)
    {
        var tex = CreateRenderable(device, width, height, mips: true);
        fixed (byte* p = bgra)
        {
            context.UpdateSubresource(tex.Texture, 0, null, (IntPtr)p, (uint)(width * 4), 0);
        }
        context.GenerateMips(tex.Srv);
        return tex;
    }

    public void Dispose()
    {
        Rtv?.Dispose();
        Srv.Dispose();
        Texture.Dispose();
    }
}
