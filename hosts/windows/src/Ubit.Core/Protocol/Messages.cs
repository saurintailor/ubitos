using System.Text.Json.Serialization;

namespace Ubit.Core.Protocol;

/// <summary>Constants of the ubit host &lt;-&gt; agent protocol, version 1 (protocol/v1.schema.json).</summary>
public static class ProtocolInfo
{
    public const int Version = 1;
    public const uint AgentVsockPort = 7710;
    public const int MaxFrameLength = 1 << 20;
    public const int MaxApps = 5000;
    public const int MaxUnknownApps = 200;
}

[JsonConverter(typeof(JsonStringEnumConverter<HostOs>))]
public enum HostOs
{
    [JsonStringEnumMemberName("macos")] Macos,
    [JsonStringEnumMemberName("windows")] Windows,
    [JsonStringEnumMemberName("linux")] Linux,
}

[JsonConverter(typeof(JsonStringEnumConverter<ItemStatus>))]
public enum ItemStatus
{
    [JsonStringEnumMemberName("native")] Native,
    [JsonStringEnumMemberName("alternative")] Alternative,
    [JsonStringEnumMemberName("workaround")] Workaround,
    [JsonStringEnumMemberName("blocker")] Blocker,
}

/// <summary>An installed app: identifiers only, never file contents.</summary>
/// <param name="Id">Windows: the Uninstall registry key name.</param>
/// <param name="Name">Display name.</param>
/// <param name="Source">Where it was found, e.g. uninstall-hklm.</param>
public sealed record App(string Id, string Name, string Source);

public sealed record ScoreItem(string CatalogId, string Category, string From, string To, ItemStatus Status);

/// <summary>Base of every message. <c>type</c> is the JSON discriminator.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Hello), "hello")]
[JsonDerivedType(typeof(HelloAck), "hello_ack")]
[JsonDerivedType(typeof(HostInventory), "host_inventory")]
[JsonDerivedType(typeof(ScoreResult), "score_result")]
[JsonDerivedType(typeof(Ping), "ping")]
[JsonDerivedType(typeof(Pong), "pong")]
[JsonDerivedType(typeof(ErrorMessage), "error")]
public abstract record Message
{
    [JsonPropertyName("v")]
    [JsonPropertyOrder(-1)]
    public int V { get; init; } = ProtocolInfo.Version;
}

public sealed record Hello(HostOs HostOs, string ShellVersion) : Message;

public sealed record HelloAck(string AgentVersion, string DistroVersion) : Message;

public sealed record HostInventory(IReadOnlyList<App> Apps, string? DefaultBrowser = null, string? DefaultMail = null) : Message;

/// <param name="Score">0-100, or null when nothing matched the catalog.</param>
public sealed record ScoreResult(int? Score, IReadOnlyList<ScoreItem> Items, IReadOnlyList<string> UnknownApps, string CatalogVersion) : Message;

public sealed record Ping(ulong Nonce) : Message;

public sealed record Pong(ulong Nonce) : Message;

public sealed record ErrorMessage(string Code, string Message) : Message;

/// <summary>
/// Source-generated serializer for the protocol: no runtime reflection, so it works in
/// trimmed or AOT builds and in hosts that switch reflection-based JSON off.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    AllowOutOfOrderMetadataProperties = true,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(Message))]
internal sealed partial class ProtocolJsonContext : JsonSerializerContext;
