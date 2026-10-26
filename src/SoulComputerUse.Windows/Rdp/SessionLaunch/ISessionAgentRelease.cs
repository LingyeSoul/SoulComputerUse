using System.Threading.Tasks;
using SoulComputerUse.Core.Protocol;

namespace SoulComputerUse.Windows.Rdp.SessionLaunch;

/// <summary>Closes the agent pipe client a ready start handed to the launch.</summary>
public interface ISessionAgentRelease
{
    /// <summary>Closes the client so no call can be sent to the agent any more.</summary>
    /// <exception cref="System.Exception">The client could not be closed.</exception>
    ValueTask ReleaseAsync(ISessionClient client);
}
