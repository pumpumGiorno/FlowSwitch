using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlowSwitch.Core.Settings;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
[JsonSerializable(typeof(FlowSwitchSettings))]
[JsonSerializable(typeof(RuntimeState))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

public static class SettingsSerializer
{
    public static string Serialize(FlowSwitchSettings settings) =>
        JsonSerializer.Serialize(settings, SettingsJsonContext.Default.FlowSwitchSettings);

    public static FlowSwitchSettings Deserialize(string json)
    {
        var settings = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.FlowSwitchSettings);
        return (settings ?? new FlowSwitchSettings()).Normalize();
    }

    public static string SerializeState(RuntimeState state) =>
        JsonSerializer.Serialize(state, SettingsJsonContext.Default.RuntimeState);

    public static RuntimeState DeserializeState(string json) =>
        JsonSerializer.Deserialize(json, SettingsJsonContext.Default.RuntimeState) ?? new RuntimeState();
}

/// <summary>Machine-written state that is not user configuration (crash history, safe mode…).</summary>
public sealed class RuntimeState
{
    public List<DateTimeOffset> RecentCrashes { get; set; } = new();
    public bool SafeMode { get; set; }
    public string? SafeModeReason { get; set; }
    public DateTimeOffset? PausedUntil { get; set; }
}
