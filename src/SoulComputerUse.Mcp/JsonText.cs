using System;
using System.Buffers;
using System.Text;
using System.Text.Json;

namespace SoulComputerUse.Mcp;

/// <summary>
/// Builds one compact JSON document as UTF-8 text. Every payload of the adapter is written through
/// this type, so escaping and field order are decided in one place and no payload is assembled by
/// string concatenation.
/// </summary>
internal static class JsonText
{
    /// <summary>Runs <paramref name="writeBody"/> against a fresh writer and returns the document.</summary>
    internal static string Write(Action<Utf8JsonWriter> writeBody)
    {
        ArgumentNullException.ThrowIfNull(writeBody);

        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writeBody(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Wraps an already serialized payload in the JSON-RPC 2.0 envelope. A null id is written as a
    /// JSON null, which is how the protocol answers a payload it could not attribute to a request.
    /// </summary>
    internal static string CreateResponse(string? idJson, string memberName, string memberJson)
    {
        ArgumentNullException.ThrowIfNull(memberName);
        ArgumentNullException.ThrowIfNull(memberJson);

        return Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WritePropertyName("id");
            if (idJson is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteRawValue(idJson);
            }

            writer.WritePropertyName(memberName);
            writer.WriteRawValue(memberJson);
            writer.WriteEndObject();
        });
    }

    /// <summary>Builds the body of a JSON-RPC error object.</summary>
    internal static string CreateErrorBody(int code, string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("code", code);
            writer.WriteString("message", message);
            writer.WriteEndObject();
        });
    }
}
