using System;

namespace SoulComputerUse.Windows.Rdp.SessionWatch;

/// <summary>
/// The production adapter: it reads whatever the supplied function returns. A caller hands it
/// <c>RdpControlHost.ConnectedState</c> without the watch ever knowing the hosted control.
/// </summary>
public sealed class FuncConnectedStateSource : IConnectedStateSource
{
    private readonly Func<int> _readConnectedState;

    /// <summary>Wraps one connected-state reader.</summary>
    /// <exception cref="ArgumentNullException">The reader is null.</exception>
    public FuncConnectedStateSource(Func<int> readConnectedState)
    {
        ArgumentNullException.ThrowIfNull(readConnectedState);
        _readConnectedState = readConnectedState;
    }

    /// <inheritdoc />
    public int ReadConnectedState()
    {
        return _readConnectedState();
    }
}