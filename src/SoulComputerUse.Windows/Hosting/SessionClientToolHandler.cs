using System;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Ipc;

namespace SoulComputerUse.Windows.Hosting;

/// <summary>
/// Adapts the shared <see cref="ISessionClient"/> to the owner-side <see cref="ISessionToolHandler"/>
/// used by the owner pipe.
/// </summary>
/// <remarks>
/// Arguments are read with the strict <see cref="SessionToolPayloads"/> codecs, so a missing field,
/// an extra field or a wrongly typed field becomes <c>E_INPUT_INVALID</c> without being coerced.
/// Every coordinate action reads one fresh status before it checks the bounds, because a cached
/// screen size could accept a coordinate the session no longer has.
/// <para>
/// This class writes no log entries. Typed text, screenshot bytes, account identifiers and paths
/// never leave the call, and the exception messages it passes on come from the sanitized contract
/// layer.
/// </para>
/// </remarks>
public sealed class SessionClientToolHandler : ISessionToolHandler
{
    private readonly ISessionClient _client;
    private readonly Action<SessionState>? _stateChanged;

    /// <summary>Creates the handler around the session client that executes the calls.</summary>
    public SessionClientToolHandler(ISessionClient client)
        : this(client, stateChanged: null)
    {
    }

    /// <summary>
    /// Creates the handler with a runtime-state observer. The observer receives <c>blocked</c> when a
    /// tool reports a protected desktop and <c>disconnected</c> when the agent pipe is lost; a failure
    /// in the observer is reported and never replaces the tool result.
    /// </summary>
    public SessionClientToolHandler(ISessionClient client, Action<SessionState>? stateChanged)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _stateChanged = stateChanged;
    }

    /// <inheritdoc />
    public async Task<SessionToolResult> HandleAsync(SessionTool tool, JsonElement arguments, CancellationToken cancellationToken)
    {
        SessionStatus? observedStatus = null;
        try
        {
            switch (tool)
            {
                case SessionTool.GetStatus:
                {
                    SessionStatus status = await _client.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                    status.Validate();
                    ReportStatusState(status);
                    return SessionToolResult.Success(SessionToolPayloads.WriteStatus(status));
                }

                case SessionTool.Screenshot:
                {
                    byte[] png = await _client.ScreenshotAsync(cancellationToken).ConfigureAwait(false);
                    if (png.Length > SessionIpcRules.ScreenshotByteLimit)
                    {
                        throw ScreenshotTooLarge(png.Length);
                    }

                    return SessionToolResult.Success(SessionToolPayloads.WriteScreenshot(png));
                }

                case SessionTool.MouseMove:
                {
                    MouseMoveArguments move = SessionToolPayloads.ReadMouseMove(arguments);
                    observedStatus = await ReadStatusAsync(cancellationToken).ConfigureAwait(false);
                    RequireCoordinate(move.X, move.Y, observedStatus);
                    await _client.MouseMoveAsync(move.X, move.Y, cancellationToken).ConfigureAwait(false);
                    return SessionToolResult.Success(SessionToolPayloads.OkResult());
                }

                case SessionTool.MouseClick:
                {
                    MouseClickArguments click = SessionToolPayloads.ReadMouseClick(arguments);
                    observedStatus = await ReadStatusAsync(cancellationToken).ConfigureAwait(false);
                    RequireCoordinate(click.X, click.Y, observedStatus);
                    await _client.MouseClickAsync(click.X, click.Y, click.Button, click.Count, cancellationToken).ConfigureAwait(false);
                    return SessionToolResult.Success(SessionToolPayloads.OkResult());
                }

                case SessionTool.MouseScroll:
                {
                    MouseScrollArguments scroll = SessionToolPayloads.ReadMouseScroll(arguments);
                    observedStatus = await ReadStatusAsync(cancellationToken).ConfigureAwait(false);
                    RequireCoordinate(scroll.X, scroll.Y, observedStatus);
                    await _client.MouseScrollAsync(
                        scroll.X,
                        scroll.Y,
                        scroll.DeltaX,
                        scroll.DeltaY,
                        cancellationToken).ConfigureAwait(false);
                    return SessionToolResult.Success(SessionToolPayloads.OkResult());
                }

                case SessionTool.KeyType:
                {
                    KeyTypeArguments type = SessionToolPayloads.ReadKeyType(arguments);
                    await _client.KeyTypeAsync(type.Text, cancellationToken).ConfigureAwait(false);
                    return SessionToolResult.Success(SessionToolPayloads.OkResult());
                }

                case SessionTool.KeyPress:
                {
                    KeyPressArguments press = SessionToolPayloads.ReadKeyPress(arguments);
                    await _client.KeyPressAsync(press.Keys, cancellationToken).ConfigureAwait(false);
                    return SessionToolResult.Success(SessionToolPayloads.OkResult());
                }

                default:
                    throw new ArgumentOutOfRangeException(nameof(tool), tool, "Unknown session tool.");
            }
        }
        catch (SessionOperationException error)
        {
            ReportFailureState(error.Code);
            SessionError sessionError = await CreateErrorAsync(error, observedStatus, cancellationToken).ConfigureAwait(false);
            return SessionToolResult.Failure(sessionError);
        }
    }

    private void ReportStatusState(SessionStatus status)
    {
        try
        {
            ReportState(SessionStateNames.FromWireName(status.State));
        }
        catch (ArgumentOutOfRangeException)
        {
            // A status with an unknown state name is refused by the caller's validation layer; this
            // observer must not turn that refusal into a second failure.
            return;
        }
    }

    private void ReportFailureState(SessionErrorCode code)
    {
        SessionState? state = code switch
        {
            SessionErrorCode.Blocked => SessionState.Blocked,
            SessionErrorCode.Disconnected or SessionErrorCode.AgentDead => SessionState.Disconnected,
            _ => null,
        };

        if (state is not null)
        {
            ReportState(state.Value);
        }
    }

    private void ReportState(SessionState state)
    {
        if (_stateChanged is null)
        {
            return;
        }

        try
        {
            _stateChanged(state);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // The runtime-state observer is a reporting channel. Its failure must not replace the
            // contract result the caller is waiting for, but it must not be silent either.
            Console.Error.WriteLine(
                "E_MANUAL_INTERVENTION A session state update failed ("
                + error.GetType().Name
                + ").");
        }
    }

    private async Task<SessionStatus> ReadStatusAsync(CancellationToken cancellationToken)
    {
        SessionStatus status = await _client.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        status.Validate();
        return status;
    }

    /// <summary>
    /// Converts one refused call into the contract error object. A status already read by the call is
    /// used when the code permits that state. A code that lists exactly one state names it without
    /// another call; any other code triggers one diagnostic status read when the call had none.
    /// </summary>
    /// <remarks>
    /// <see cref="SessionError"/> cannot represent an omitted state for a code that requires one.
    /// The last-resort fallback therefore uses the first state the catalog permits for that code.
    /// That happens when the observed status is absent or names a state the code does not allow, and
    /// the diagnostic read could not supply an allowed state either.
    /// </remarks>
    private async Task<SessionError> CreateErrorAsync(
        SessionOperationException error,
        SessionStatus? observedStatus,
        CancellationToken cancellationToken)
    {
        SessionErrorRule rule = SessionErrorCatalog.For(error.Code);
        SessionState? state = TryReadState(observedStatus);
        if (state is not null && rule.AllowsState(state))
        {
            return new SessionError(error.Code, error.Message, error.Retryable, state);
        }

        if (observedStatus is null && !cancellationToken.IsCancellationRequested && NeedsStatusRead(rule))
        {
            SessionStatus? lateStatus = await TryReadStatusAsync(cancellationToken).ConfigureAwait(false);
            SessionState? lateState = TryReadState(lateStatus);
            if (lateState is not null && rule.AllowsState(lateState))
            {
                state = lateState;
            }
        }

        if (state is null || !rule.AllowsState(state))
        {
            state = FallbackState(rule);
        }

        return new SessionError(error.Code, error.Message, error.Retryable, state);
    }

    /// <summary>
    /// Reads a status only to complete an error object. A failure of this diagnostic read never
    /// replaces the original tool failure; the caller falls back to the code's permitted state.
    /// </summary>
    private async Task<SessionStatus?> TryReadStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            SessionStatus status = await _client.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            status.Validate();
            return status;
        }
        catch (SessionOperationException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// Tells whether a diagnostic status read can add information. A code that lists exactly one
    /// state already names it, and an agent self-exit code has no host state at all, so neither case
    /// needs a second call.
    /// </summary>
    private static bool NeedsStatusRead(SessionErrorRule rule)
    {
        if (rule.StateScope == SessionErrorStateScope.AgentSelfExit)
        {
            return false;
        }

        return rule.StateScope != SessionErrorStateScope.ListedStates || rule.States.Count != 1;
    }

    private static SessionState? TryReadState(SessionStatus? status)
    {
        if (status is null)
        {
            return null;
        }

        try
        {
            return SessionStateNames.FromWireName(status.State);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static SessionState? FallbackState(SessionErrorRule rule)
    {
        if (rule.StateScope == SessionErrorStateScope.AgentSelfExit)
        {
            return null;
        }

        if (rule.StateScope == SessionErrorStateScope.ListedStates && rule.States.Count > 0)
        {
            return rule.States[0];
        }

        return SessionState.Starting;
    }

    private static void RequireCoordinate(int x, int y, SessionStatus status)
    {
        try
        {
            SessionToolArguments.RequireCoordinate(x, y, status.Screen);
        }
        catch (ArgumentOutOfRangeException error)
        {
            throw SessionToolArguments.CoordinateOutOfRange(error);
        }
    }

    private static SessionOperationException ScreenshotTooLarge(int byteCount)
    {
        string message = "The encoded screenshot is "
            + byteCount.ToString(CultureInfo.InvariantCulture)
            + " bytes, which exceeds the contract limit of "
            + SessionIpcRules.ScreenshotByteLimit.ToString(CultureInfo.InvariantCulture)
            + " bytes.";
        return new SessionOperationException(SessionErrorCode.ScreenshotTooLarge, message, false);
    }
}