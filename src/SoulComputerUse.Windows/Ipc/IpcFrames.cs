using System;
using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SoulComputerUse.Windows.Ipc;

/// <summary>
/// A frame on the local named pipe did not follow the contract of section 4 of the interface
/// contract. The exception is thrown by the codec and never silently turned into a success.
/// </summary>
public sealed class IpcProtocolException : Exception
{
    /// <summary>Creates the exception for one refused frame.</summary>
    public IpcProtocolException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception for one refused frame with the error that caused the refusal.</summary>
    public IpcProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// One request frame: <c>{ "v": 1, "type": "request", "id": 7, "method": "screenshot", "params": {} }</c>.
/// The parameters are a detached JSON element, so the frame stays valid after its source document is
/// disposed.
/// </summary>
public sealed record IpcRequest(int Id, string Method, JsonElement Parameters);

/// <summary>
/// The sanitized error object of section 2 of the interface contract as it travels on the pipe:
/// <c>{ "code": "E_...", "message": "...", "retryable": false, "state": "ready" }</c>.
/// </summary>
/// <remarks>
/// <c>State</c> is null only for transport-generated protocol errors. The pipe transport does not own
/// the session state machine, so it cannot name a state for a frame it refused; inventing one would
/// be a false claim. Every error the session itself produces carries its real state.
/// </remarks>
public sealed record IpcError(string Code, string Message, bool Retryable, string? State)
{
    /// <summary>Creates the error object for a frame the transport itself refused.</summary>
    public static IpcError Protocol(string message)
    {
        return new IpcError("E_IPC_PROTOCOL", message, false, null);
    }

    /// <summary>Projects a contract error object onto its wire shape.</summary>
    public static IpcError FromSessionError(SoulComputerUse.Core.Session.SessionError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        string? state = error.State is null
            ? null
            : SoulComputerUse.Core.Session.SessionStateNames.ToWireName(error.State.Value);
        return new IpcError(
            SoulComputerUse.Core.Session.SessionErrorCodeNames.ToWireName(error.Code),
            error.Message,
            error.Retryable,
            state);
    }
}

/// <summary>One response frame. A success carries a result, a failure carries an error, never both.</summary>
public sealed record IpcResponse(int Id, bool Ok, JsonElement? Result, IpcError? Error)
{
    /// <summary>Creates a success response around one result payload.</summary>
    public static IpcResponse CreateSuccess(int id, JsonElement result)
    {
        return new IpcResponse(id, true, result, null);
    }

    /// <summary>Creates a failure response around one error object.</summary>
    public static IpcResponse CreateFailure(int id, IpcError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new IpcResponse(id, false, null, error);
    }
}

/// <summary>One event frame, such as the agent's heartbeat. Clients read and ignore events they do not handle.</summary>
public sealed record IpcEvent(string Name, JsonElement Data);

/// <summary>What one line turned out to be.</summary>
public enum IpcFrameKind
{
    /// <summary>The line is not a usable frame; see the parse result's error message.</summary>
    Invalid = 0,

    /// <summary>A request frame.</summary>
    Request = 1,

    /// <summary>A response frame.</summary>
    Response = 2,

    /// <summary>An event frame.</summary>
    Event = 3,
}

/// <summary>
/// The result of reading one line. A refused line still reports the id it carried, when that id could
/// be recovered, so the peer can correlate the protocol error with its request.
/// </summary>
public sealed record IpcFrameParseResult(
    IpcFrameKind Kind,
    IpcRequest? Request,
    IpcResponse? Response,
    IpcEvent? Event,
    int? RecoveredId,
    string? ErrorMessage)
{
    /// <summary>Creates a refused result.</summary>
    public static IpcFrameParseResult Invalid(int? recoveredId, string errorMessage)
    {
        return new IpcFrameParseResult(IpcFrameKind.Invalid, null, null, null, recoveredId, errorMessage);
    }

    /// <summary>Creates a request result.</summary>
    public static IpcFrameParseResult ForRequest(IpcRequest request)
    {
        return new IpcFrameParseResult(IpcFrameKind.Request, request, null, null, request.Id, null);
    }

    /// <summary>Creates a response result.</summary>
    public static IpcFrameParseResult ForResponse(IpcResponse response)
    {
        return new IpcFrameParseResult(IpcFrameKind.Response, null, response, null, response.Id, null);
    }

    /// <summary>Creates an event result.</summary>
    public static IpcFrameParseResult ForEvent(IpcEvent frame)
    {
        return new IpcFrameParseResult(IpcFrameKind.Event, null, null, frame, null, null);
    }
}

/// <summary>
/// Encodes and decodes the single-line JSON frames of section 4 of the interface contract: UTF-8,
/// one frame per line, terminated by <c>\n</c>. Parsing is a pure function so every refusal rule is
/// testable without a pipe.
/// </summary>
/// <remarks>
/// The rules are fail-closed: a frame whose <c>v</c> is not 1, whose <c>type</c> is not one of the
/// three defined kinds, that has no integer <c>id</c>, or whose JSON is broken is refused with an
/// <c>E_IPC_PROTOCOL</c> message. Nothing is guessed from a partial frame.
/// </remarks>
public static class IpcFrameCodec
{
    /// <summary>The only protocol version this milestone speaks.</summary>
    public const int Version = 1;

    private const string RequestType = "request";
    private const string ResponseType = "response";
    private const string EventType = "event";

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 64,
    };

    /// <summary>Serializes one request frame as a single line without its terminator.</summary>
    public static string WriteRequest(IpcRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Write(writer =>
        {
            WriteHeader(writer, RequestType);
            writer.WriteNumber("id", request.Id);
            writer.WriteString("method", request.Method);
            writer.WritePropertyName("params");
            request.Parameters.WriteTo(writer);
        });
    }

    /// <summary>Serializes one response frame as a single line without its terminator.</summary>
    public static string WriteResponse(IpcResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return Write(writer =>
        {
            WriteHeader(writer, ResponseType);
            writer.WriteNumber("id", response.Id);
            writer.WriteBoolean("ok", response.Ok);
            if (response.Ok)
            {
                writer.WritePropertyName("result");
                JsonElement result = response.Result
                    ?? throw new IpcProtocolException("A success response needs a result payload.");
                result.WriteTo(writer);
                return;
            }

            IpcError error = response.Error
                ?? throw new IpcProtocolException("A failure response needs an error object.");
            writer.WritePropertyName("error");
            WriteError(writer, error);
        });
    }

    /// <summary>Serializes one event frame as a single line without its terminator.</summary>
    public static string WriteEvent(IpcEvent frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return Write(writer =>
        {
            WriteHeader(writer, EventType);
            writer.WriteString("event", frame.Name);
            writer.WritePropertyName("data");
            frame.Data.WriteTo(writer);
        });
    }

    /// <summary>
    /// Reads one line. A malformed line yields <see cref="IpcFrameKind.Invalid"/> instead of an
    /// exception, because the peer is untrusted and its frame must never crash the reader.
    /// </summary>
    public static IpcFrameParseResult Parse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line, DocumentOptions);
        }
        catch (JsonException)
        {
            return IpcFrameParseResult.Invalid(null, "The IPC frame is not valid JSON.");
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return IpcFrameParseResult.Invalid(null, "The IPC frame must be a JSON object.");
            }

            if (!TryReadVersion(root, out string? versionError))
            {
                return IpcFrameParseResult.Invalid(RecoveredId(root), versionError!);
            }

            if (!root.TryGetProperty("type", out JsonElement typeElement) || typeElement.ValueKind != JsonValueKind.String)
            {
                return IpcFrameParseResult.Invalid(RecoveredId(root), "The IPC frame has no string type.");
            }

            string? type = typeElement.GetString();
            return type switch
            {
                RequestType => ParseRequest(root),
                ResponseType => ParseResponse(root),
                EventType => ParseEvent(root),
                _ => IpcFrameParseResult.Invalid(RecoveredId(root), "The IPC frame type is unknown; only request, response and event are defined."),
            };
        }
    }

    private static IpcFrameParseResult ParseRequest(JsonElement root)
    {
        if (!TryReadRequiredId(root, out int id, out string idError))
        {
            return IpcFrameParseResult.Invalid(RecoveredId(root), idError);
        }

        if (!root.TryGetProperty("method", out JsonElement methodElement) || methodElement.ValueKind != JsonValueKind.String)
        {
            return IpcFrameParseResult.Invalid(id, "The IPC request has no string method.");
        }

        JsonElement parameters;
        if (root.TryGetProperty("params", out JsonElement paramsElement))
        {
            if (paramsElement.ValueKind != JsonValueKind.Object)
            {
                return IpcFrameParseResult.Invalid(id, "The IPC request parameters must be a JSON object.");
            }

            parameters = paramsElement.Clone();
        }
        else
        {
            // An absent parameter object means "no arguments", which is what the empty tools take.
            using JsonDocument empty = JsonDocument.Parse("{}");
            parameters = empty.RootElement.Clone();
        }

        return IpcFrameParseResult.ForRequest(new IpcRequest(id, methodElement.GetString()!, parameters));
    }

    private static IpcFrameParseResult ParseResponse(JsonElement root)
    {
        if (!TryReadRequiredId(root, out int id, out string idError))
        {
            return IpcFrameParseResult.Invalid(RecoveredId(root), idError);
        }

        if (!root.TryGetProperty("ok", out JsonElement okElement) || okElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return IpcFrameParseResult.Invalid(id, "The IPC response has no boolean ok.");
        }

        if (okElement.GetBoolean())
        {
            if (!root.TryGetProperty("result", out JsonElement resultElement))
            {
                return IpcFrameParseResult.Invalid(id, "The IPC success response has no result.");
            }

            return IpcFrameParseResult.ForResponse(IpcResponse.CreateSuccess(id, resultElement.Clone()));
        }

        if (!root.TryGetProperty("error", out JsonElement errorElement) || errorElement.ValueKind != JsonValueKind.Object)
        {
            return IpcFrameParseResult.Invalid(id, "The IPC failure response has no error object.");
        }

        IpcError? error = ReadError(errorElement);
        return error is null
            ? IpcFrameParseResult.Invalid(id, "The IPC error object is incomplete; code, message and retryable are required.")
            : IpcFrameParseResult.ForResponse(IpcResponse.CreateFailure(id, error));
    }

    private static IpcFrameParseResult ParseEvent(JsonElement root)
    {
        if (!root.TryGetProperty("event", out JsonElement eventElement) || eventElement.ValueKind != JsonValueKind.String)
        {
            return IpcFrameParseResult.Invalid(null, "The IPC event has no string name.");
        }

        JsonElement data;
        if (root.TryGetProperty("data", out JsonElement dataElement))
        {
            data = dataElement.Clone();
        }
        else
        {
            using JsonDocument empty = JsonDocument.Parse("{}");
            data = empty.RootElement.Clone();
        }

        return IpcFrameParseResult.ForEvent(new IpcEvent(eventElement.GetString()!, data));
    }

    private static IpcError? ReadError(JsonElement errorElement)
    {
        if (!errorElement.TryGetProperty("code", out JsonElement codeElement) || codeElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        if (!errorElement.TryGetProperty("message", out JsonElement messageElement) || messageElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        if (!errorElement.TryGetProperty("retryable", out JsonElement retryableElement)
            || retryableElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return null;
        }

        string? state = null;
        if (errorElement.TryGetProperty("state", out JsonElement stateElement) && stateElement.ValueKind == JsonValueKind.String)
        {
            state = stateElement.GetString();
        }

        return new IpcError(codeElement.GetString()!, messageElement.GetString()!, retryableElement.GetBoolean(), state);
    }

    private static bool TryReadVersion(JsonElement root, out string? errorMessage)
    {
        if (!root.TryGetProperty("v", out JsonElement versionElement) || versionElement.ValueKind != JsonValueKind.Number)
        {
            errorMessage = "The IPC frame has no numeric version.";
            return false;
        }

        if (!versionElement.TryGetInt32(out int version))
        {
            errorMessage = "The IPC frame version must be an integer.";
            return false;
        }

        if (version != Version)
        {
            errorMessage = "The IPC frame version must be "
                + Version.ToString(CultureInfo.InvariantCulture)
                + ", but "
                + version.ToString(CultureInfo.InvariantCulture)
                + " was received.";
            return false;
        }

        errorMessage = null;
        return true;
    }

    private static bool TryReadRequiredId(JsonElement root, out int id, out string errorMessage)
    {
        id = 0;
        if (!root.TryGetProperty("id", out JsonElement idElement))
        {
            errorMessage = "The IPC frame has no id.";
            return false;
        }

        if (idElement.ValueKind != JsonValueKind.Number || !idElement.TryGetInt32(out id))
        {
            errorMessage = "The IPC frame id must be a 32-bit integer.";
            return false;
        }

        errorMessage = string.Empty;
        return true;
    }

    private static int? RecoveredId(JsonElement root)
    {
        if (root.TryGetProperty("id", out JsonElement idElement)
            && idElement.ValueKind == JsonValueKind.Number
            && idElement.TryGetInt32(out int id))
        {
            return id;
        }

        return null;
    }

    private static void WriteHeader(Utf8JsonWriter writer, string frameType)
    {
        writer.WriteStartObject();
        writer.WriteNumber("v", Version);
        writer.WriteString("type", frameType);
    }

    private static void WriteError(Utf8JsonWriter writer, IpcError error)
    {
        writer.WriteStartObject();
        writer.WriteString("code", error.Code);
        writer.WriteString("message", error.Message);
        writer.WriteBoolean("retryable", error.Retryable);
        if (error.State is null)
        {
            writer.WriteNull("state");
        }
        else
        {
            writer.WriteString("state", error.State);
        }

        writer.WriteEndObject();
    }

    private static string Write(Action<Utf8JsonWriter> writeFrame)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writeFrame(writer);
            writer.WriteEndObject();
            writer.Flush();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
