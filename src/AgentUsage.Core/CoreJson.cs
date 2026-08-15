using System.Text.Json.Serialization;
using AgentUsage.Providers;

namespace AgentUsage;

/// <summary>
/// Source-generated serialisation. Reflection-based JSON is switched off in both consumers —
/// under NativeAOT it does not merely warn, it fails on the user's machine — so every type that
/// crosses a JSON boundary has to be listed here.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(AuthStatus))]
[JsonSerializable(typeof(CliResult))]
[JsonSerializable(typeof(Snapshot))]
[JsonSerializable(typeof(CodexEvent))]
public partial class CoreJson : JsonSerializerContext;
