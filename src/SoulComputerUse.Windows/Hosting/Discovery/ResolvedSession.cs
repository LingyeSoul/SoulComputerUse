using System;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Protocol;

namespace SoulComputerUse.Windows.Hosting.Discovery;

/// <summary>
/// One resolved session together with whatever has to be released when the caller is done with it.
/// </summary>
/// <remarks>
/// Ownership of the client moves to this object. On the first <see cref="DisposeAsync"/> the client is
/// closed when it supports asynchronous disposal and the optional release callback runs exactly once;
/// later calls do nothing. Both steps run even when the first of them fails, so a release is never
/// skipped because closing the connection failed. The callback is how a caller releases state that
/// belongs to a session it started; a client attached to an existing owner passes no callback.
/// </remarks>
public sealed class ResolvedSession : IAsyncDisposable
{
    private readonly ISessionClient _client;
    private readonly Func<ValueTask>? _releaseAsync;
    private int _disposed;

    /// <summary>Creates one resolved session.</summary>
    /// <param name="client">The client whose ownership moves to this object.</param>
    /// <param name="releaseAsync">An optional step that runs once on disposal, or null when nothing needs releasing.</param>
    /// <exception cref="ArgumentNullException">The client is null.</exception>
    public ResolvedSession(ISessionClient client, Func<ValueTask>? releaseAsync)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _releaseAsync = releaseAsync;
    }

    /// <summary>The client to call tools through.</summary>
    public ISessionClient Client => _client;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (_client is IAsyncDisposable disposableClient)
            {
                await disposableClient.DisposeAsync();
            }
        }
        finally
        {
            if (_releaseAsync is not null)
            {
                await _releaseAsync();
            }
        }
    }
}
