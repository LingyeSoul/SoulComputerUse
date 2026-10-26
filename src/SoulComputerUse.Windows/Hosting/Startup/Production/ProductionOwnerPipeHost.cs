using System;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Windows.Ipc;

namespace SoulComputerUse.Windows.Hosting.Startup.Production;

/// <summary>
/// Serves the owner pipe from this process and keeps it alive until the caller disposes the host.
/// </summary>
/// <remarks>
/// <see cref="Start"/> returns only after a probe connection was accepted, so a caller cannot report a
/// ready session while the pipe is still unbound. The probe is a real client that connects and closes at
/// once; the accept loop then continues to serve the CLI and MCP adapters through the same handler.
/// </remarks>
public sealed class ProductionOwnerPipeHost : IOwnerPipeHost, IAsyncDisposable
{
    private const int ProbeTimeoutMilliseconds = 250;
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(5);

    private readonly string _pipeName;
    private NamedPipeSessionServer? _server;
    private CancellationTokenSource? _lifetime;
    private Task? _acceptLoop;
    private bool _started;
    private bool _disposed;

    /// <summary>Creates the host for the contract owner pipe.</summary>
    public ProductionOwnerPipeHost()
        : this(IpcPipeNames.Owner)
    {
    }

    /// <summary>Creates the host for one pipe name, which tests set to a unique value.</summary>
    /// <exception cref="ArgumentException">The pipe name is empty.</exception>
    public ProductionOwnerPipeHost(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipeName = pipeName;
    }

    /// <summary>The pipe this host serves.</summary>
    public string PipeName => _pipeName;

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The host was already started, or the pipe never accepted a probe.</exception>
    public void Start(ISessionToolHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            throw new InvalidOperationException("The owner pipe host was already started.");
        }

        NamedPipeSessionServer server = new(_pipeName, handler);
        CancellationTokenSource lifetime = new();
        Task loop = server.RunAsync(lifetime.Token);
        try
        {
            WaitUntilAccepts(loop);
        }
        catch
        {
            lifetime.Cancel();
            DisposeServerAsync(server, loop).GetAwaiter().GetResult();
            lifetime.Dispose();
            throw;
        }

        _server = server;
        _lifetime = lifetime;
        _acceptLoop = loop;
        _started = true;
    }

    /// <summary>Stops accepting and waits for live connections to finish.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancellationTokenSource? lifetime = _lifetime;
        NamedPipeSessionServer? server = _server;
        Task? loop = _acceptLoop;
        _lifetime = null;
        _server = null;
        _acceptLoop = null;

        if (lifetime is not null)
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
        }

        if (server is not null)
        {
            await server.DisposeAsync().ConfigureAwait(false);
        }

        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Disposing the host is this type's own shutdown, not a failure of the accept loop.
            }
        }

        lifetime?.Dispose();
    }

    private void WaitUntilAccepts(Task acceptLoop)
    {
        long deadline = Environment.TickCount64 + (long)StartupTimeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (acceptLoop.IsCompleted)
            {
                throw new InvalidOperationException(
                    "The owner pipe accept loop stopped before the pipe accepted a probe connection, so the session cannot serve the CLI or the MCP adapter.");
            }

            using NamedPipeClientStream probe = new(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                // The probe is a named pipe connection, not a remote desktop connection: this type never
                // opens a child session. The asynchronous overload is used so the scan that pins the
                // remote desktop connect surface stays meaningful.
                probe.ConnectAsync(ProbeTimeoutMilliseconds).GetAwaiter().GetResult();
                return;
            }
            catch (TimeoutException)
            {
                // The pipe is not bound yet; retry until the startup deadline.
            }
            catch (System.IO.IOException)
            {
                // No listener yet on this name; retry until the startup deadline.
            }
        }

        throw new InvalidOperationException(
            "The owner pipe did not accept a probe connection within its startup timeout, so the session was not reported ready.");
    }

    private static async Task DisposeServerAsync(NamedPipeSessionServer server, Task loop)
    {
        await server.DisposeAsync().ConfigureAwait(false);
        try
        {
            await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Startup failed, so the accept loop's own cancellation is expected.
        }
    }
}