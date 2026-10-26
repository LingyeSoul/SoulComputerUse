using System;
using System.Threading.Tasks;

namespace SoulComputerUse.Windows.Rdp.SessionLaunch;

/// <summary>The production owner-pipe release: disposes the host that serves the pipe.</summary>
public sealed class OwnerPipeRelease : IOwnerPipeRelease
{
    private readonly IAsyncDisposable _ownerPipeHost;

    /// <summary>Creates the release over the owner pipe host the startup pipeline started.</summary>
    /// <exception cref="ArgumentNullException">The host is null.</exception>
    public OwnerPipeRelease(IAsyncDisposable ownerPipeHost)
    {
        ArgumentNullException.ThrowIfNull(ownerPipeHost);
        _ownerPipeHost = ownerPipeHost;
    }

    /// <inheritdoc />
    public ValueTask ReleaseAsync()
    {
        return _ownerPipeHost.DisposeAsync();
    }
}
