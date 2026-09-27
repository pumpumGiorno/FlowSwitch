using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using FlowSwitch.Core.Diagnostics;
using FlowSwitch.Core.Scene;
using FlowSwitch.Diagnostics;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace FlowSwitch.Rendering;

/// <summary>
/// Compiles FlowSwitch.hlsl — embedded in FlowSwitch.dll, so no file has to exist next to the
/// app — once with the system's d3dcompiler_47.dll and caches the bytecode under
/// %LOCALAPPDATA%\FlowSwitch\ShaderCache, so later starts create shaders in a few milliseconds.
/// A cache entry that cannot be read or turned into a shader is discarded and recompiled.
/// </summary>
internal sealed class ShaderLibrary : IDisposable
{
    public const string ResourceName = "FlowSwitch.Shaders.FlowSwitch.hlsl";

    private readonly ID3D11PixelShader[] _pixel = new ID3D11PixelShader[6];
    private readonly string _source;
    private readonly byte[] _sourceBytes;
    private readonly string _hash;
    private readonly string? _cacheDirectory;
    private int _compiled;
    private int _cached;

    public ShaderLibrary(ID3D11Device device, string cacheDirectory)
    {
        _source = LoadSource();
        _sourceBytes = Encoding.UTF8.GetBytes(_source);
        _hash = Convert.ToHexString(SHA256.HashData(_sourceBytes))[..16];
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            _cacheDirectory = cacheDirectory;
        }
        catch (Exception ex)
        {
            Log.Warn($"Shader cache folder unavailable ({cacheDirectory}): {ex.Message} — compiling every start.");
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        QuadVS = Vertex(device, "VSQuad");
        FullscreenVS = Vertex(device, "VSFullscreen");
        _pixel[(int)ShaderKind.Backdrop] = Pixel(device, "PSBackdrop");
        _pixel[(int)ShaderKind.Card] = Pixel(device, "PSCard");
        _pixel[(int)ShaderKind.Sprite] = Pixel(device, "PSSprite");
        _pixel[(int)ShaderKind.Glow] = Pixel(device, "PSGlow");
        _pixel[(int)ShaderKind.Orbit] = Pixel(device, "PSOrbit");
        _pixel[(int)ShaderKind.Pill] = Pixel(device, "PSPill");
        Downsample = Pixel(device, "PSDownsample");
        Resample = Pixel(device, "PSResample");
        Log.Info($"Shaders ready in {sw.ElapsedMilliseconds} ms (10 entry points: {_compiled} compiled, {_cached} from cache; source {ResourceName} #{_hash}).");
    }

    public ID3D11VertexShader QuadVS { get; }
    public ID3D11VertexShader FullscreenVS { get; }
    public ID3D11PixelShader Downsample { get; }
    public ID3D11PixelShader Resample { get; }

    public ID3D11PixelShader Pixel(ShaderKind kind) => _pixel[(int)kind];

    private ID3D11VertexShader Vertex(ID3D11Device device, string entry) =>
        Create(entry, "vs_5_0", bytes => device.CreateVertexShader(bytes));

    private ID3D11PixelShader Pixel(ID3D11Device device, string entry) =>
        Create(entry, "ps_5_0", bytes => device.CreatePixelShader(bytes));

    private T Create<T>(string entry, string profile, Func<byte[], T> create)
    {
        string? cachePath = _cacheDirectory is null ? null : Path.Combine(_cacheDirectory, $"{_hash}-{entry}.cso");
        if (cachePath is not null && File.Exists(cachePath))
        {
            try
            {
                var shader = create(File.ReadAllBytes(cachePath));
                _cached++;
                return shader;
            }
            catch (Exception ex)
            {
                Log.Warn($"Cached shader {entry} is unusable ({ErrorText.Of(ex)}) — recompiling.");
                try { File.Delete(cachePath); } catch { /* recompiled below either way */ }
            }
        }

        byte[] bytecode = Compile(entry, profile);
        _compiled++;
        T result = GraphicsDevice.Stage($"Create shader {entry} ({profile})", () => create(bytecode));
        if (cachePath is not null)
        {
            try
            {
                File.WriteAllBytes(cachePath, bytecode);
            }
            catch (Exception ex)
            {
                Log.Debug($"Shader cache write failed: {ex.Message}");
            }
        }
        return result;
    }

    private byte[] Compile(string entry, string profile)
    {
        Blob? code = null;
        Blob? errors = null;
        try
        {
            // Pass the exact UTF-8 bytes. The string overload hands D3DCompile the length in
            // characters, which truncates any source containing non-ASCII text ("unexpected end of file").
            var result = Compiler.Compile(_sourceBytes, Array.Empty<ShaderMacro>(), null!, entry, "FlowSwitch.hlsl", profile,
                ShaderFlags.OptimizationLevel3, EffectFlags.None, out code, out errors);
            string messages = errors?.AsString()?.Trim() ?? string.Empty;
            if (result.Failure || code is null)
                throw new GraphicsInitException($"D3DCompile {entry} ({profile})", $"{ErrorText.HResult(result.Code)}{(messages.Length > 0 ? " — " + messages : string.Empty)}");
            if (messages.Length > 0) Log.Debug($"Shader {entry} compiled with messages: {messages}");
            return code.AsBytes();
        }
        catch (GraphicsInitException)
        {
            throw;
        }
        catch (DllNotFoundException ex)
        {
            throw new GraphicsInitException("Load d3dcompiler_47.dll", ex.Message, ex);
        }
        catch (Exception ex)
        {
            throw new GraphicsInitException($"D3DCompile {entry} ({profile})", ErrorText.Of(ex), ex);
        }
        finally
        {
            code?.Dispose();
            errors?.Dispose();
        }
    }

    private static string LoadSource()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
                           ?? throw new GraphicsInitException("Load shader source", $"embedded resource {ResourceName} is missing from FlowSwitch.dll");
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
