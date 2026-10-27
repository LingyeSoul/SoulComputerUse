using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Hosting.Discovery;
using SoulComputerUse.Windows.Hosting.Launch;

namespace SoulComputerUse.Mcp;

/// <summary>
/// Maps one MCP tool call onto a session resolved for that call and renders the contract's section 5
/// result envelope. Coordinates are checked against a status read taken at the start of the same
/// call: the adapter keeps no screen cache, because a stale size would accept a coordinate the
/// session no longer has. A failed call is a tool result with <c>isError</c>, never a JSON-RPC error.
/// </summary>
/// <remarks>
/// The session is resolved per call and disposed when the call ends, so one tool call costs one more
/// named-pipe connection than a process-lifetime client would. That cost is accepted deliberately: in
/// exchange the adapter survives having no owner at all, which is what makes <c>start_session</c>
/// reachable, and it never holds a client whose owner has since gone away.
/// </remarks>
internal sealed class McpToolExecutor
{
    /// <summary>The contract's per-frame ceiling, counted on the encoded PNG bytes (5 MiB).</summary>
    private const int MaximumScreenshotBytes = 5 * 1024 * 1024;

    private readonly ISessionAccessor _sessions;
    private readonly IHostStarter _hostStarter;
    private readonly TextWriter _diagnostics;

    internal McpToolExecutor(ISessionAccessor sessions, IHostStarter hostStarter, TextWriter diagnostics)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(hostStarter);
        ArgumentNullException.ThrowIfNull(diagnostics);

        _sessions = sessions;
        _hostStarter = hostStarter;
        _diagnostics = diagnostics;
    }

    /// <summary>Runs one session tool and returns the serialized MCP tool result.</summary>
    internal async Task<string> ExecuteAsync(
        SessionTool tool,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        SessionStatus? status = null;
        SessionOperationException? failure = null;
        string result;
        try
        {
            await using ResolvedSession session = await _sessions.ResolveAsync(cancellationToken).ConfigureAwait(false);
            SessionStatus observed = await session.Client.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            observed.Validate();
            status = observed;
            result = await InvokeAsync(session.Client, tool, arguments, observed, cancellationToken).ConfigureAwait(false);
        }
        catch (SessionOperationException error)
        {
            failure = error;
            result = ErrorResult(error, status);
        }

        stopwatch.Stop();
        SessionState? state = status is null ? null : TryParseState(status.State);
        McpLog.ToolCall(_diagnostics, SessionToolNames.ToWireName(tool), stopwatch.ElapsedMilliseconds, failure?.Code, state);
        return result;
    }

    /// <summary>Runs one adapter-local tool and returns the serialized MCP tool result.</summary>
    internal async Task<string> ExecuteLocalAsync(McpLocalTool tool, CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        SessionOperationException? failure = null;
        string result;
        try
        {
            result = tool switch
            {
                McpLocalTool.StartSession => await StartSessionAsync(cancellationToken).ConfigureAwait(false),
                _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, "Unknown local tool."),
            };
        }
        catch (SessionOperationException error)
        {
            failure = error;
            result = ErrorResult(error, null);
        }

        stopwatch.Stop();
        McpLog.ToolCall(_diagnostics, McpLocalToolTable.NameOf(tool), stopwatch.ElapsedMilliseconds, failure?.Code, null);
        return result;
    }

    private async Task<string> StartSessionAsync(CancellationToken cancellationToken)
    {
        await _hostStarter.StartAsync(cancellationToken).ConfigureAwait(false);
        return OkResult();
    }

    private static async Task<string> InvokeAsync(
        ISessionClient client,
        SessionTool tool,
        JsonElement? arguments,
        SessionStatus status,
        CancellationToken cancellationToken)
    {
        ToolArgumentReader reader = ToolArgumentReader.Read(tool, arguments);
        switch (tool)
        {
            case SessionTool.GetStatus:
                return TextResult(StatusJson(status));

            case SessionTool.Screenshot:
            {
                byte[] frame = await client.ScreenshotAsync(cancellationToken).ConfigureAwait(false);
                if (frame.Length > MaximumScreenshotBytes)
                {
                    throw new SessionOperationException(
                        SessionErrorCode.ScreenshotTooLarge,
                        $"The encoded screenshot is {frame.Length} bytes, which exceeds the {MaximumScreenshotBytes}-byte limit.",
                        false);
                }

                return ImageResult(frame);
            }

            case SessionTool.MouseMove:
            {
                int x = reader.RequireInt32("x");
                int y = reader.RequireInt32("y");
                RequireScreenCoordinate(status, x, y);
                await client.MouseMoveAsync(x, y, cancellationToken).ConfigureAwait(false);
                return OkResult();
            }

            case SessionTool.MouseClick:
            {
                int x = reader.RequireInt32("x");
                int y = reader.RequireInt32("y");
                MouseButton button = reader.RequireMouseButton("button");
                int count = reader.RequireInt32("count");
                RequireClickCount(count);
                RequireScreenCoordinate(status, x, y);
                await client.MouseClickAsync(x, y, button, count, cancellationToken).ConfigureAwait(false);
                return OkResult();
            }

            case SessionTool.MouseScroll:
            {
                int x = reader.RequireInt32("x");
                int y = reader.RequireInt32("y");
                int deltaX = reader.RequireInt32("deltaX");
                int deltaY = reader.RequireInt32("deltaY");
                RequireScreenCoordinate(status, x, y);
                await client.MouseScrollAsync(x, y, deltaX, deltaY, cancellationToken).ConfigureAwait(false);
                return OkResult();
            }

            case SessionTool.KeyType:
            {
                string text = reader.RequireString("text");
                RequireText(text);
                await client.KeyTypeAsync(text, cancellationToken).ConfigureAwait(false);
                return OkResult();
            }

            case SessionTool.KeyPress:
            {
                IReadOnlyList<SessionKey> keys = reader.RequireKeys("keys");
                await client.KeyPressAsync(keys, cancellationToken).ConfigureAwait(false);
                return OkResult();
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(tool), tool, "Unknown session tool.");
        }
    }

    /// <summary>
    /// Checks the coordinate against the screen the session just reported. The check is skipped
    /// when the session is not in a state that admits input, so the owner's state error is reported
    /// instead of masking it with a coordinate error the owner never had a chance to see.
    /// </summary>
    private static void RequireScreenCoordinate(SessionStatus status, int x, int y)
    {
        SessionState state = ParseState(status.State);
        if (state is not (SessionState.Ready or SessionState.Blocked))
        {
            return;
        }

        try
        {
            SessionToolArguments.RequireCoordinate(x, y, status.Screen);
        }
        catch (ArgumentOutOfRangeException error)
        {
            throw SessionToolArguments.CoordinateOutOfRange(error);
        }
    }

    private static void RequireClickCount(int count)
    {
        try
        {
            SessionToolArguments.RequireClickCount(count);
        }
        catch (ArgumentOutOfRangeException error)
        {
            throw SessionToolArguments.InputInvalid(error);
        }
    }

    private static void RequireText(string text)
    {
        try
        {
            SessionToolArguments.RequireText(text);
        }
        catch (ArgumentOutOfRangeException error)
        {
            throw SessionToolArguments.InputInvalid(error);
        }
    }

    private static string StatusJson(SessionStatus status)
    {
        return JsonText.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("state", status.State);
            writer.WriteString("sessionId", status.SessionId);
            writer.WriteString("authTier", AuthTierNames.ToWireName(status.AuthTier));
            writer.WriteString("gateStrength", GateStrengthNames.ToWireName(status.GateStrength));
            writer.WritePropertyName("screen");
            writer.WriteStartObject();
            writer.WriteNumber("width", status.Screen.Width);
            writer.WriteNumber("height", status.Screen.Height);
            writer.WriteEndObject();
            if (status.HelloHint is not null)
            {
                writer.WriteString("helloHint", status.HelloHint);
            }

            writer.WriteEndObject();
        });
    }

    private static string OkResult()
    {
        return TextResult("{\"ok\":true}");
    }

    private static string TextResult(string text)
    {
        return JsonText.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartArray("content");
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", text);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    private static string ImageResult(byte[] frame)
    {
        return JsonText.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartArray("content");
            writer.WriteStartObject();
            writer.WriteString("type", "image");
            writer.WriteString("data", Convert.ToBase64String(frame));
            writer.WriteString("mimeType", "image/png");
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    private static string ErrorResult(SessionOperationException error, SessionStatus? status)
    {
        return JsonText.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartArray("content");
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", SessionErrorJson.Create(error, status));
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteBoolean("isError", true);
            writer.WriteEndObject();
        });
    }

    private static SessionState ParseState(string wireName)
    {
        try
        {
            return SessionStateNames.FromWireName(wireName);
        }
        catch (ArgumentOutOfRangeException error)
        {
            throw new InvalidOperationException(
                "The session reported a state name that the interface contract does not define.",
                error);
        }
    }

    private static SessionState? TryParseState(string wireName)
    {
        try
        {
            return SessionStateNames.FromWireName(wireName);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
