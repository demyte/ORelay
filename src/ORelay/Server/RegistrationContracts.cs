using System.Text.Json.Serialization;

namespace ORelay.Server;

/// <summary>JSON body accepted by POST /registrations.</summary>
public sealed record CreateRegistrationRequest(
    [property: JsonPropertyName("callbackUrl")] string? CallbackUrl);

/// <summary>Public registration data returned by management operations.</summary>
public sealed record RegistrationResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("callbackUrl")] string CallbackUrl,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("leaseSeconds")] int LeaseSeconds,
    [property: JsonPropertyName("relayCallbackUrl")] string RelayCallbackUrl);

/// <summary>Safe error body. Callback values and routing state are never echoed.</summary>
public sealed record RelayErrorResponse(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

public sealed record RelayServerReadyResponse(
    [property: JsonPropertyName("identity")] string Identity,
    [property: JsonPropertyName("bind")] string Bind,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("relayCallbackUrl")] string RelayCallbackUrl);

/// <summary>
/// Source-generated metadata used by the endpoint layer and available to the
/// host when it configures its JSON options for Native AOT.
/// </summary>
[JsonSerializable(typeof(CreateRegistrationRequest))]
[JsonSerializable(typeof(RegistrationResponse))]
[JsonSerializable(typeof(RelayErrorResponse))]
[JsonSerializable(typeof(RelayHealthResponse))]
[JsonSerializable(typeof(RelayServerReadyResponse))]
public partial class RelayJsonContext : JsonSerializerContext;
