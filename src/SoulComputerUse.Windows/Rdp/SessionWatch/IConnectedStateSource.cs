namespace SoulComputerUse.Windows.Rdp.SessionWatch;

/// <summary>
/// The one read a watch needs from the remote desktop control: the connected-state number the client
/// reports. The control is kept behind this port so the poll can run without a hosted control.
/// </summary>
public interface IConnectedStateSource
{
    /// <summary>
    /// Reads the client's connected-state number: 0 idle, 1 connected, 2 connecting, 3 waiting for
    /// the operator to log on. Values outside that set are refused, never guessed.
    /// </summary>
    int ReadConnectedState();
}