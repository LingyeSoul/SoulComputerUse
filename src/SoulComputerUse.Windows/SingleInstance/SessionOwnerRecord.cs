using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using SoulComputerUse.Core.Session;

namespace SoulComputerUse.Windows.SingleInstance;

/// <summary>
/// The ownership record from the interface contract. It exists for diagnostics and for the
/// abandoned-lock recovery rules, which is why its field set is fixed: no credentials, no user
/// names, no window titles, no clipboard content and no screenshots ever belong here.
/// Every field is required, because a record with invented defaults would feed nonsense into the
/// recovery rules.
/// </summary>
public sealed record SessionOwnerRecord(
    [property: JsonPropertyName("sessionId")] [property: JsonRequired] string SessionId,
    [property: JsonPropertyName("hostPid")] [property: JsonRequired] int HostPid,
    [property: JsonPropertyName("hostProcessStartTime")] [property: JsonRequired] DateTimeOffset HostProcessStartTime,
    [property: JsonPropertyName("subagentWtsSessionId")] [property: JsonRequired] int SubagentWtsSessionId,
    [property: JsonPropertyName("childSessionsOpenedByUs")] [property: JsonRequired] bool ChildSessionsOpenedByUs,
    [property: JsonPropertyName("state")] [property: JsonRequired] [property: JsonConverter(typeof(SessionStateWireNameConverter))] SessionState State,
    [property: JsonPropertyName("acquiredAt")] [property: JsonRequired] DateTimeOffset AcquiredAt);

/// <summary>Serializes a <see cref="SessionState"/> with the contract wire name.</summary>
internal sealed class SessionStateWireNameConverter : JsonConverter<SessionState>
{
    public override SessionState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? wireName = reader.GetString();
        if (wireName is null)
        {
            throw new JsonException("The owner record field 'state' must be a string.");
        }

        try
        {
            return SessionStateNames.FromWireName(wireName);
        }
        catch (ArgumentOutOfRangeException error)
        {
            throw new JsonException($"The owner record field 'state' has the unknown value '{wireName}'.", error);
        }
    }

    public override void Write(Utf8JsonWriter writer, SessionState value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(SessionStateNames.ToWireName(value));
    }
}