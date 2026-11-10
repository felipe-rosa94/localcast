using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalCast.Core.Protocol;

public static class ProtocolSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize<T>(T message) where T : SignalingMessage
    {
        return JsonSerializer.Serialize<SignalingMessage>(message, Options);
    }

    public static SignalingMessage? Deserialize(string json)
    {
        return JsonSerializer.Deserialize<SignalingMessage>(json, Options);
    }
}
