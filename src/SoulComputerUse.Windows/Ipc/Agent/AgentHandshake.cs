using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using SoulComputerUse.Core.Protocol;

namespace SoulComputerUse.Windows.Ipc.Agent;

/// <summary>
/// The agent pipe handshake and heartbeat payloads of section 4 of the interface contract. The outer
/// frames are the shared <see cref="IpcFrameCodec"/> frames; this type owns only the <c>hello</c>
/// event data, the <c>hello_ack</c> request, and the heartbeat tick.
/// </summary>
/// <remarks>
/// The hello data carries a second version field because the event is validated by the client
/// before any request is sent. A mismatch in the event version, the capability list, or the nonce
/// is refused explicitly; nothing is guessed from a partial hello.
/// </remarks>
public static class AgentHandshake
{
    /// <summary>The event name of the first frame the agent sends.</summary>
    public const string HelloEvent = "hello";

    /// <summary>The event name of the periodic liveness frame.</summary>
    public const string HeartbeatEvent = "heartbeat";

    /// <summary>The request method the host sends to finish the handshake.</summary>
    public const string HelloAckMethod = "hello_ack";

    /// <summary>The version carried inside the hello event data.</summary>
    public const int HandshakeVersion = 1;

    private static readonly ReadOnlyCollection<string> _capabilities = Array.AsReadOnly(
        new string[] { "screen", "mouse", "keyboard" });

    /// <summary>The capabilities this transport speaks, in the contract's order.</summary>
    public static IReadOnlyList<string> Capabilities => _capabilities;

    /// <summary>Encodes the first event the agent sends, including the one-time nonce.</summary>
    /// <exception cref="ArgumentException"><paramref name="nonce"/> is empty or whitespace.</exception>
    public static string WriteHello(string nonce)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);

        JsonElement data = Encode(writer =>
        {
            writer.WriteNumber("v", HandshakeVersion);
            writer.WriteStartArray("capabilities");
            foreach (string capability in _capabilities)
            {
                writer.WriteStringValue(capability);
            }

            writer.WriteEndArray();
            writer.WriteString("nonce", nonce);
        });

        return IpcFrameCodec.WriteEvent(new IpcEvent(HelloEvent, data));
    }

    /// <summary>Encodes one heartbeat event with the next increasing tick.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tick"/> is not positive.</exception>
    public static string WriteHeartbeat(long tick)
    {
        if (tick <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tick), tick, "The heartbeat tick must be positive.");
        }

        JsonElement data = Encode(writer => writer.WriteNumber("tick", tick));
        return IpcFrameCodec.WriteEvent(new IpcEvent(HeartbeatEvent, data));
    }

    /// <summary>Creates the <c>hello_ack</c> request with the empty parameter object.</summary>
    public static IpcRequest CreateHelloAck(int id)
    {
        return new IpcRequest(id, HelloAckMethod, SessionToolPayloads.EmptyArguments());
    }

    /// <summary>
    /// Tells whether a request is a well-formed <c>hello_ack</c>. The parameters must be an empty
    /// object; an extra parameter is a protocol violation rather than useful data.
    /// </summary>
    public static bool IsHelloAck(IpcRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.Method, HelloAckMethod, StringComparison.Ordinal))
        {
            return false;
        }

        if (request.Parameters.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return !request.Parameters.EnumerateObject().Any();
    }

    /// <summary>
    /// Validates the hello event against the handshake version, the exact capability list and the
    /// expected nonce. A nonce mismatch is <c>E_IPC_PEER</c>; every other refusal is
    /// <c>E_IPC_PROTOCOL</c>.
    /// </summary>
    /// <exception cref="SessionOperationException">The event is not an acceptable hello.</exception>
    public static void ValidateHello(IpcEvent frame, string expectedNonce)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(expectedNonce);

        if (!string.Equals(frame.Name, HelloEvent, StringComparison.Ordinal))
        {
            throw AgentErrors.Protocol("The child-session agent's first frame was not a hello event.");
        }

        JsonElement data = frame.Data;
        if (data.ValueKind != JsonValueKind.Object)
        {
            throw AgentErrors.Protocol("The child-session agent's hello data is not a JSON object.");
        }

        if (!data.TryGetProperty("v", out JsonElement versionElement)
            || versionElement.ValueKind != JsonValueKind.Number
            || !versionElement.TryGetInt32(out int version)
            || version != HandshakeVersion)
        {
            throw AgentErrors.Protocol("The child-session agent's hello data has an unsupported version.");
        }

        if (!HasExactCapabilities(data))
        {
            throw AgentErrors.Protocol(
                "The child-session agent's hello capabilities are not exactly the contract's screen, mouse and keyboard list.");
        }

        if (!data.TryGetProperty("nonce", out JsonElement nonceElement) || nonceElement.ValueKind != JsonValueKind.String)
        {
            throw AgentErrors.Protocol("The child-session agent's hello data has no string nonce.");
        }

        string? nonce = nonceElement.GetString();
        if (nonce is null)
        {
            throw AgentErrors.Protocol("The child-session agent's hello data has no string nonce.");
        }

        if (!AgentNonce.Matches(expectedNonce, nonce))
        {
            throw AgentErrors.Peer("The child-session agent's hello nonce does not match this session, so the connection was refused.");
        }
    }

    private static bool HasExactCapabilities(JsonElement data)
    {
        if (!data.TryGetProperty("capabilities", out JsonElement capabilities)
            || capabilities.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        int index = 0;
        foreach (JsonElement capability in capabilities.EnumerateArray())
        {
            if (index >= _capabilities.Count || capability.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            string? name = capability.GetString();
            if (!string.Equals(name, _capabilities[index], StringComparison.Ordinal))
            {
                return false;
            }

            index++;
        }

        return index == _capabilities.Count;
    }

    private static JsonElement Encode(Action<Utf8JsonWriter> writeProperties)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writeProperties(writer);
            writer.WriteEndObject();
            writer.Flush();
        }

        using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }
}