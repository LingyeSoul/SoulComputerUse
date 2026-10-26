using System.Threading.Tasks;

namespace SoulComputerUse.Windows.Rdp.SessionLaunch;

/// <summary>Stops the owner pipe a ready start left serving CLI and MCP callers.</summary>
public interface IOwnerPipeRelease
{
    /// <summary>Stops accepting and stops waiting on the owner pipe.</summary>
    /// <exception cref="System.Exception">The pipe host could not be stopped.</exception>
    ValueTask ReleaseAsync();
}
