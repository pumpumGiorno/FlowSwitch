using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using FlowSwitch.Core.Diagnostics;
using FlowSwitch.Core.Scene;
using Vortice.D3DCompiler;
using Vortice.Direct3D11;

namespace FlowSwitch.Rendering;

/// <summary>
/// Compiles FlowSwitch.hlsl (embedded) once and caches the bytecode on disk, so later starts
/// create shaders in a few milliseconds.
/// </summary>
internal sealed class ShaderLibrary : IDisposable
{
    private readonly ID3D11PixelShader[] _pixel = new ID3D11PixelShader[6];

    public ShaderLibrary(ID3D11Device device, string cacheDirectory)
    {
        string source = LoadSource();
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..16];
        Directory.CreateDirectory(cacheDirectory);

        byte[] Get(string entry, string profile)
        {
            string path = Path.Combine(cacheDirectory, $"{hash}-{entry}.cso");
            try
            {
                if (File.Exists(path)) return File.ReadAllBytes(path);
            }
            catch
            {
                // Recompile.
            }

            var bytecode = Compiler.Compile(source, entry, "FlowSwitch.hlsl", profile,
                ShaderFlags.OptimizationLevel3, EffectFlags.None).ToArray();
            try
            {
                File.WriteAllBytes(path, bytecode);
            }
            catch (Exception ex)
            {
                Log.Debug($"Shader cache write failed: {ex.Message}");
            }
            return bytecode;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        QuadVS = device.CreateVertexShader(Get("VSQuad", "vs_5_0"));
        FullscreenVS = device.CreateVertexShader(Get("VSFullscreen", "vs_5_0"));
        _pixel[(int)ShaderKind.Backdrop] = device.CreatePixelShader(Get("PSBackdrop", "ps_5_0"));
        _pixel[(int)ShaderKind.Card] = device.CreatePixelShader(Get("PSCard", "ps_5_0"));
        _pixel[(int)ShaderKind.Sprite] = device.CreatePixelShader(Get("PSSprite", "ps_5_0"));
        _pixel[(int)ShaderKind.Glow] = device.CreatePixelShader(Get("PSGlow", "ps_5_0"));
        _pixel[(int)ShaderKind.Orbit] = device.CreatePixelShader(Get("PSOrbit", "ps_5_0"));
        _pixel[(int)ShaderKind.Pill] = device.CreatePixelShader(Get("PSPill", "ps_5_0"));
        Downsample = device.CreatePixelShader(Get("PSDownsample", "ps_5_0"));
        Resample = device.CreatePixelShader(Get("PSResample", "ps_5_0"));
        Log.Info($"Shaders ready in {sw.ElapsedMilliseconds} ms.");
    }

    public ID3D11VertexShader QuadVS { get; }
    public ID3D11VertexShader FullscreenVS { get; }
    public ID3D11PixelShader Downsample { get; }
    public ID3D11PixelShader Resample { get; }

    public ID3D11PixelShader Pixel(ShaderKind kind) => _pixel[(int)kind];

    private static string LoadSource()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("FlowSwitch.Shaders.FlowSwitch.hlsl")
                           ?? throw new InvalidOperationException("Embedded shader source missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public void Dispose()
    {
        QuadVS.Dispose();
        FullscreenVS.Dispose();
        foreach (var ps in _pixel) ps?.Dispose();
        Downsample.Dispose();
        Resample.Dispose();
    }
}
