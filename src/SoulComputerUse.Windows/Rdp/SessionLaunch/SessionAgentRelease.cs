using System;
using System.Threading.Tasks;
using SoulComputerUse.Core.Protocol;

namespace SoulComputerUse.Windows.Rdp.SessionLaunch;

/// <summary>The production agent release: a client that cannot be closed is an explicit failure.</summary>
public sealed class SessionAgentRelease : ISessionAgentRelease
{
    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// The client is not disposable, so the pipe cannot be closed and the caller would keep a
    /// connection it can no longer control.
    /// </exception>
    public ValueTask ReleaseAsync(ISessionClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        if (client is IAsyncDisposable disposable)
        {
            return disposable.DisposeAsync();
        }

        throw new InvalidOperationException(
            "The agent client cannot be closed because it is not disposable, so the launch cannot release the agent connection.");
    }
}
