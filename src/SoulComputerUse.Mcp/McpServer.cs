using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Windows.Hosting.Launch;

namespace SoulComputerUse.Mcp;

/// <summary>
/// The MCP stdio adapter of the interface contract's section 5. It reads newline-delimited JSON-RPC
/// 2.0 messages, publishes the session tool surface from <see cref="SessionToolCatalog"/> plus the
/// adapter-local <see cref="McpLocalToolTable"/>, and maps every session tool onto a session resolved
/// for that one call.
/// </summary>
/// <remarks>
/// A tool that fails is a tool result with <c>isError</c> and the contract's error object as text;
/// only a malformed JSON-RPC message becomes a JSON-RPC error. The adapter speaks one message at a
/// time and holds no session state of its own: it owns neither the state machine nor the screen size.
/// </remarks>
public sealed class McpServer
{
    private const string ServerName = "soul-computer-use";
    private const string ServerVersion = "0.1.0";

    /// <summary>The protocol revision the adapter answers with when the client asks for an unknown one.</summary>
    public const string DefaultProtocolVersion = "2024-11-05";

    private static readonly string[] SupportedProtocolVersions = ["2024-11-05", "2025-03-26", "2025-06-18"];

    private static readonly string[] KnownMethods = ["initialize", "notifications/initialized", "ping", "tools/list", "tools/call"];

    private readonly McpToolExecutor _executor;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly TextWriter _diagnostics;

    /// <summary>Creates one server over the given transport, session accessor and host starter.</summary>
    public McpServer(
        ISessionAccessor sessions,
        IHostStarter hostStarter,
        TextReader input,
        TextWriter output,
        TextWriter diagnostics)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(hostStarter);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(diagnostics);

        _executor = new McpToolExecutor(sessions, hostStarter, diagnostics);
        _input = input;
        _output = output;
        _diagnostics = diagnostics;
    }

    /// <summary>
    /// Serves messages until the input ends or cancellation is requested. Each response is flushed
    /// before the next message is read, so a client never waits on buffered protocol output.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            string? line;
            try
            {
                line = await _input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (line is null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string? response = await HandleLineAsync(line, cancellationToken).ConfigureAwait(false);
            if (response is null)
            {
                continue;
            }

            await _output.WriteLineAsync(response.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string?> HandleLineAsync(string line, CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return ErrorResponse(null, JsonRpcErrorCodes.ParseError, "The message is not valid JSON.");
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return ErrorResponse(null, JsonRpcErrorCodes.InvalidRequest, "A JSON-RPC message must be a JSON object.");
            }

            if (!TryReadId(root, out bool hasId, out string idJson))
            {
                return ErrorResponse(null, JsonRpcErrorCodes.InvalidRequest, "The 'id' member must be a string, a number or null.");
            }

            if (!IsVersion2(root))
            {
                return hasId ? ErrorResponse(idJson, JsonRpcErrorCodes.InvalidRequest, "The 'jsonrpc' member must be exactly \"2.0\".") : null;
            }

            if (!root.TryGetProperty("method", out JsonElement methodElement) || methodElement.ValueKind != JsonValueKind.String)
            {
                return hasId ? ErrorResponse(idJson, JsonRpcErrorCodes.InvalidRequest, "The 'method' member must be a string.") : null;
            }

            string method = methodElement.GetString() ?? string.Empty;
            if (method.Length == 0)
            {
                return hasId ? ErrorResponse(idJson, JsonRpcErrorCodes.InvalidRequest, "The 'method' member must not be empty.") : null;
            }

            JsonElement? parameters = root.TryGetProperty("params", out JsonElement paramsElement) ? paramsElement : null;

            string result;
            try
            {
                result = await DispatchAsync(method, parameters, cancellationToken).ConfigureAwait(false);
            }
            catch (McpRequestException error)
            {
                McpLog.RequestFailed(_diagnostics, KnownOrNull(method), stopwatch.ElapsedMilliseconds, "warning");
                return hasId ? ErrorResponse(idJson, error.Code, error.Message) : null;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                McpLog.RequestFailed(_diagnostics, KnownOrNull(method), stopwatch.ElapsedMilliseconds, "error");
                return hasId ? ErrorResponse(idJson, JsonRpcErrorCodes.InternalError, "The server could not complete the request.") : null;
            }

            // A notification never receives a response, so an executed notification is simply dropped.
            return hasId ? JsonText.CreateResponse(idJson, "result", result) : null;
        }
    }

    private async Task<string> DispatchAsync(string method, JsonElement? parameters, CancellationToken cancellationToken)
    {
        switch (method)
        {
            case "initialize":
                return Initialize(parameters);

            case "notifications/initialized":
                // MCP sends this as a notification; an empty result also answers a client that
                // mistakenly puts an id on it instead of leaving the request unanswered.
                return "{}";

            case "ping":
                return "{}";

            case "tools/list":
                return ToolsList();

            case "tools/call":
            {
                McpToolSelection selection = ReadTool(parameters);
                JsonElement? arguments = ReadArguments(parameters);
                if (selection.LocalTool is McpLocalTool localTool)
                {
                    RequireNoArguments(localTool, arguments);
                    return await _executor.ExecuteLocalAsync(localTool, cancellationToken).ConfigureAwait(false);
                }

                if (selection.SessionTool is SessionTool sessionTool)
                {
                    return await _executor.ExecuteAsync(sessionTool, arguments, cancellationToken).ConfigureAwait(false);
                }

                throw new InvalidOperationException(
                    "The tool selection named neither a session tool nor a local tool.");
            }

            default:
                throw new McpRequestException(
                    JsonRpcErrorCodes.MethodNotFound,
                    "The method is not part of this server's surface; call tools/list to see the accepted tools.");
        }
    }

    private static string Initialize(JsonElement? parameters)
    {
        string protocolVersion = DefaultProtocolVersion;
        if (parameters is not null)
        {
            JsonElement value = parameters.Value;
            if (value.ValueKind != JsonValueKind.Object)
            {
                throw new McpRequestException(JsonRpcErrorCodes.InvalidParams, "The 'initialize' parameters must be a JSON object.");
            }

            if (value.TryGetProperty("protocolVersion", out JsonElement requested))
            {
                if (requested.ValueKind != JsonValueKind.String)
                {
                    throw new McpRequestException(JsonRpcErrorCodes.InvalidParams, "The 'protocolVersion' parameter must be a string.");
                }

                string requestedVersion = requested.GetString() ?? string.Empty;
                if (IsSupportedProtocolVersion(requestedVersion))
                {
                    protocolVersion = requestedVersion;
                }
            }
        }

        return JsonText.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("protocolVersion", protocolVersion);
            writer.WritePropertyName("capabilities");
            writer.WriteStartObject();
            writer.WritePropertyName("tools");
            writer.WriteStartObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WritePropertyName("serverInfo");
            writer.WriteStartObject();
            writer.WriteString("name", ServerName);
            writer.WriteString("version", ServerVersion);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });
    }

    private static string ToolsList()
    {
        return JsonText.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartArray("tools");
            foreach (SessionTool tool in SessionToolCatalog.Tools)
            {
                writer.WriteStartObject();
                writer.WriteString("name", SessionToolNames.ToWireName(tool));
                writer.WriteString("description", SessionToolCatalog.Describe(tool));
                writer.WritePropertyName("inputSchema");
                writer.WriteRawValue(SessionToolCatalog.SchemaOf(tool));
                writer.WriteEndObject();
            }

            foreach (McpLocalTool tool in McpLocalToolTable.Tools)
            {
                writer.WriteStartObject();
                writer.WriteString("name", McpLocalToolTable.NameOf(tool));
                writer.WriteString("description", McpLocalToolTable.Describe(tool));
                writer.WritePropertyName("inputSchema");
                writer.WriteRawValue(McpLocalToolTable.SchemaOf(tool));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    private static McpToolSelection ReadTool(JsonElement? parameters)
    {
        if (parameters is null || parameters.Value.ValueKind != JsonValueKind.Object)
        {
            throw new McpRequestException(
                JsonRpcErrorCodes.InvalidParams,
                "The 'tools/call' parameters must be a JSON object with a 'name' member.");
        }

        if (!parameters.Value.TryGetProperty("name", out JsonElement nameElement)
            || nameElement.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(nameElement.GetString()))
        {
            throw new McpRequestException(JsonRpcErrorCodes.InvalidParams, "The 'tools/call' parameter 'name' must be a non-empty string.");
        }

        string toolName = nameElement.GetString() ?? string.Empty;
        if (McpLocalToolTable.TryFind(toolName, out McpLocalTool localTool))
        {
            return new McpToolSelection(null, localTool);
        }

        try
        {
            return new McpToolSelection(SessionToolNames.FromWireName(toolName), null);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new McpRequestException(
                JsonRpcErrorCodes.InvalidParams,
                "The requested tool is not part of this server; call tools/list to see the accepted tools.");
        }
    }

    private static JsonElement? ReadArguments(JsonElement? parameters)
    {
        if (parameters is null
            || !parameters.Value.TryGetProperty("arguments", out JsonElement arguments)
            || arguments.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new McpRequestException(JsonRpcErrorCodes.InvalidParams, "The 'tools/call' parameter 'arguments' must be a JSON object.");
        }

        return arguments;
    }

    /// <summary>
    /// Refuses an argument supplied to an adapter-local tool. Every local tool publishes the empty
    /// object schema, so a supplied name is a request the published schema does not accept; the
    /// message names the tool and never a supplied value.
    /// </summary>
    private static void RequireNoArguments(McpLocalTool tool, JsonElement? arguments)
    {
        if (arguments is null || !arguments.Value.EnumerateObject().Any())
        {
            return;
        }

        throw new McpRequestException(
            JsonRpcErrorCodes.InvalidParams,
            $"The tool '{McpLocalToolTable.NameOf(tool)}' takes no arguments.");
    }

    private static bool TryReadId(JsonElement root, out bool hasId, out string idJson)
    {
        hasId = false;
        idJson = "null";
        if (!root.TryGetProperty("id", out JsonElement element))
        {
            return true;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.String:
            case JsonValueKind.Number:
                hasId = true;
                idJson = element.GetRawText();
                return true;

            case JsonValueKind.Null:
                hasId = true;
                return true;

            default:
                return false;
        }
    }

    private static bool IsVersion2(JsonElement root)
    {
        return root.TryGetProperty("jsonrpc", out JsonElement version)
            && version.ValueKind == JsonValueKind.String
            && string.Equals(version.GetString(), "2.0", StringComparison.Ordinal);
    }

    private static bool IsSupportedProtocolVersion(string version)
    {
        return SupportedProtocolVersions.Contains(version, StringComparer.Ordinal);
    }

    private static string? KnownOrNull(string method)
    {
        return KnownMethods.Contains(method, StringComparer.Ordinal) ? method : null;
    }

    private static string ErrorResponse(string? idJson, int code, string message)
    {
        return JsonText.CreateResponse(idJson, "error", JsonText.CreateErrorBody(code, message));
    }
}