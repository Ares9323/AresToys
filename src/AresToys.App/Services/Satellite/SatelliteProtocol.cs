using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AresToys.App.Services.Satellite;

/// <summary>One line on the satellite channel. <c>Kind</c> is <see cref="SatelliteProtocol.KindRequest"/>,
/// <see cref="SatelliteProtocol.KindResponse"/> or <see cref="SatelliteProtocol.KindEvent"/>. A request and its answer
/// share <c>Id</c>; an event is fire and forget. <c>Ok</c> false on an answer carries a short <c>Error</c>.</summary>
public sealed record SatelliteMessage(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("data")] JsonElement? Data = null,
    [property: JsonPropertyName("ok")] bool Ok = true,
    [property: JsonPropertyName("error")] string? Error = null);

/// <summary>Names and wire format of the channel between the main (possibly elevated) AresToys and the satellite, the
/// non elevated copy of the same exe that hosts Wormholes and the Launcher. One JSON object per line.</summary>
public static class SatelliteProtocol
{
    public const string Arg = "--satellite";
    public const string ModulesPrefix = "--modules=";
    public const string MainPidPrefix = "--main-pid=";

    public const string KindRequest = "req";
    public const string KindResponse = "res";
    public const string KindEvent = "evt";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>One pipe per user, so two accounts on the same machine never meet.</summary>
    public static string PipeNameFor(SecurityIdentifier user) => $"AresToys.Satellite.{user.Value}";

    public static string PipeName()
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("No user SID.");
        return PipeNameFor(user);
    }

    public static string Serialize(SatelliteMessage message) => JsonSerializer.Serialize(message, Options);

    /// <summary>Null when the line is not a valid message.</summary>
    public static SatelliteMessage? Parse(string line)
    {
        try { return JsonSerializer.Deserialize<SatelliteMessage>(line, Options); }
        catch (JsonException) { return null; }
    }

    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);
}
