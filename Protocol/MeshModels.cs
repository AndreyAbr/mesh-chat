using System.Text.Json.Serialization;

namespace MeshChat.Protocol;

public sealed record BeaconPacket(
    [property: JsonPropertyName("nodeId")] string NodeId,
    [property: JsonPropertyName("tcpPort")] int TcpPort,
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion = 1);

public sealed class MeshEnvelope
{
    [JsonPropertyName("msgId")]
    public Guid MsgId { get; set; } = Guid.NewGuid();

    [JsonPropertyName("type")]
    public string Type { get; set; } = "CHAT"; // CHAT | ACK

    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    [JsonPropertyName("to")]
    public string To { get; set; } = "*";

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("ts")]
    public long TimestampMs { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [JsonPropertyName("ttl")]
    public int Ttl { get; set; } = 5;

    [JsonPropertyName("path")]
    public List<string> Path { get; set; } = [];
}

public sealed record PeerInfo(
    string NodeId,
    string Address,
    int TcpPort,
    DateTime LastSeenUtc);

public sealed record DeliveredMessage(
    Guid MsgId,
    string Type,
    string From,
    string To,
    string Text,
    long TimestampMs,
    IReadOnlyList<string> Path,
    DateTime ReceivedAtUtc);
