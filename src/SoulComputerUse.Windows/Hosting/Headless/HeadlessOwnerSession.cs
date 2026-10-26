using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Protocol;

namespace SoulComputerUse.Windows.Hosting.Headless;

/// <summary>
/// The session a headless start produced: a client on the owner pipe, the owner that serves it, and
/// the session client the pipe executes calls through.
/// </summary>
/// <remarks>
/// <para>
/// Tool calls go to the forward client, which is a pipe connection, not to the inner session client.
/// That keeps the calling side identical to the path that attaches to an already running owner, and it
/// means a caller cannot reach the backend around the owner's validation.
/// </para>
/// <para>
/// <see cref="DisposeAsync"/> closes the forward client first, so no new call is sent while the owner
/// shuts down, then stops the owner, which deletes the ownership record, stops the pipe and releases
/// the single-instance lock. The inner session client is closed last, when nothing can call it any
/// more. Every step is attempted even when an earlier one fails, the whole sequence runs once, and the
/// collected failures are thrown together instead of being swallowed.
/// </para>
/// </remarks>
public sealed class HeadlessOwnerSession : ISessionClient, IAsyncDisposable
{
    private readonly ISessionClient _forwardClient;
    private readonly SessionOwnerService _owner;
    private readonly ISessionClient _sessionClient;
    private int _disposed;

    /// <summary>Creates the session around the parts a headless start produced.</summary>
    /// <exception cref="ArgumentNullException">A part is null.</exception>
    internal HeadlessOwnerSession(ISessionClient forwardClient, SessionOwnerService owner, ISessionClient sessionClient)
    {
        ArgumentNullException.ThrowIfNull(forwardClient);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(sessionClient);
        _forwardClient = forwardClient;
        _owner = owner;
        _sessionClient = sessionClient;
    }

    /// <inheritdoc />
    public Task<SessionStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        return _forwardClient.GetStatusAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<byte[]> ScreenshotAsync(CancellationToken cancellationToken)
    {
        return _forwardClient.ScreenshotAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task MouseMoveAsync(int x, int y, CancellationToken cancellationToken)
    {
        return _forwardClient.MouseMoveAsync(x, y, cancellationToken);
    }

    /// <inheritdoc />
    public Task MouseClickAsync(int x, int y, MouseButton button, int count, CancellationToken cancellationToken)
    {
        return _forwardClient.MouseClickAsync(x, y, button, count, cancellationToken);
    }

    /// <inheritdoc />
    public Task MouseScrollAsync(int x, int y, int deltaX, int deltaY, CancellationToken cancellationToken)
    {
        return _forwardClient.MouseScrollAsync(x, y, deltaX, deltaY, cancellationToken);
    }

    /// <inheritdoc />
    public Task KeyTypeAsync(string text, CancellationToken cancellationToken)
    {
        return _forwardClient.KeyTypeAsync(text, cancellationToken);
    }

    /// <inheritdoc />
    public Task KeyPressAsync(IReadOnlyList<SessionKey> keys, CancellationToken cancellationToken)
    {
        return _forwardClient.KeyPressAsync(keys, cancellationToken);
    }

    /// <summary>
    /// Closes the forward client, stops the owner and releases the lock, then closes the inner session
    /// client. Idempotent; failures are collected and thrown together after every step ran.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        List<Exception> errors = [];
        await CollectAsync(errors, DisposeForwardClientAsync).ConfigureAwait(false);
        await CollectAsync(errors, DisposeOwnerAsync).ConfigureAwait(false);
        await CollectAsync(errors, DisposeSessionClientAsync).ConfigureAwait(false);

        if (errors.Count > 0)
        {
            throw new AggregateException(
                "The headless session could not release every resource, so the ownership record or the single-instance lock may remain.",
                errors);
        }
    }

    private static async ValueTask CollectAsync(List<Exception> errors, Func<ValueTask> step)
    {
        try
        {
            await step().ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            errors.Add(error);
        }
    }

    private ValueTask DisposeForwardClientAsync()
    {
        return _forwardClient is IAsyncDisposable disposable
            ? disposable.DisposeAsync()
            : ValueTask.CompletedTask;
    }

    private ValueTask DisposeOwnerAsync()
    {
        return _owner.DisposeAsync();
    }

    private ValueTask DisposeSessionClientAsync()
    {
        return _sessionClient is IAsyncDisposable disposable
            ? disposable.DisposeAsync()
            : ValueTask.CompletedTask;
    }
}
