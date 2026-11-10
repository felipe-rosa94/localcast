using System.Text.Json.Serialization;

namespace LocalCast.Core.Protocol;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AuthRequestMessage), typeDiscriminator: "auth")]
[JsonDerivedType(typeof(AuthOkMessage), typeDiscriminator: "auth_ok")]
[JsonDerivedType(typeof(ErrorMessage), typeDiscriminator: "error")]
[JsonDerivedType(typeof(SdpOfferMessage), typeDiscriminator: "sdp_offer")]
[JsonDerivedType(typeof(SdpAnswerMessage), typeDiscriminator: "sdp_answer")]
[JsonDerivedType(typeof(IceCandidateMessage), typeDiscriminator: "ice_candidate")]
[JsonDerivedType(typeof(DisconnectMessage), typeDiscriminator: "disconnect")]
[JsonDerivedType(typeof(CursorUpdateMessage), typeDiscriminator: "cursor")]
public abstract record SignalingMessage;

public record AuthRequestMessage(
    [property: JsonPropertyName("pin")] string Pin,
    [property: JsonPropertyName("clientName")] string ClientName,
    [property: JsonPropertyName("version")] string Version = ProtocolConstants.ProtocolVersion
) : SignalingMessage;

public record AuthOkMessage(
    [property: JsonPropertyName("sessionToken")] string SessionToken,
    [property: JsonPropertyName("tvName")] string TvName
) : SignalingMessage;

public record ErrorMessage(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message
) : SignalingMessage;

public record SdpOfferMessage(
    [property: JsonPropertyName("sdp")] string Sdp
) : SignalingMessage;

public record SdpAnswerMessage(
    [property: JsonPropertyName("sdp")] string Sdp
) : SignalingMessage;

public record IceCandidateMessage(
    [property: JsonPropertyName("candidate")] string Candidate,
    [property: JsonPropertyName("sdpMid")] string? SdpMid,
    [property: JsonPropertyName("sdpMLineIndex")] int? SdpMLineIndex
) : SignalingMessage;

public record DisconnectMessage(
    [property: JsonPropertyName("reason")] string Reason
) : SignalingMessage;

public record CursorUpdateMessage(
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("visible")] bool Visible,
    [property: JsonPropertyName("t")] long TimestampMs
) : SignalingMessage;
