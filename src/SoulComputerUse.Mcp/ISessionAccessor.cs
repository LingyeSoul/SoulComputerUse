using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Hosting.Discovery;

namespace SoulComputerUse.Mcp;

/// <summary>
/// Resolves the session one tool call should use, at the moment the call arrives.
/// </summary>
/// <remarks>
/// The adapter resolves per call instead of binding one session at startup, so the process lives as
/// long as its protocol session even when no owner is running. Callers own the returned
/// <see cref="ResolvedSession"/> and must dispose it when the call ends.
/// </remarks>
public interface ISessionAccessor
{
    /// <summary>Resolves one session for the duration of a single tool call.</summary>
    /// <param name="cancellationToken">Cancels the probe, the connection or the start.</param>
    /// <returns>The session to use; the caller disposes it.</returns>
    /// <exception cref="SessionOperationException">No session could be resolved.</exception>
    Task<ResolvedSession> ResolveAsync(CancellationToken cancellationToken);
}
