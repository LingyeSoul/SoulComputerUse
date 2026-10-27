using System;

namespace SoulComputerUse.Windows.Hosting.Launch;

/// <summary>
/// A started host process as the launcher observes it: its id, whether it is still running, and the
/// exit code once it has ended.
/// </summary>
/// <remarks>
/// <para>
/// The handle is observation only. The launcher must be able to tell "still starting" from "exited
/// before it took the lock", so a start that silently produced a dead process can never be reported
/// as success.
/// </para>
/// <para>
/// <see cref="HasExited"/> and <see cref="ExitCode"/> are non-destructive: reading either is a plain
/// query that closes nothing, so the launcher may ask again on every poll. Implementations are not
/// required to be thread safe; the launcher reads them from a single thread for the life of the
/// handle.
/// </para>
/// </remarks>
public interface IHostProcessHandle : IDisposable
{
    /// <summary>The started process's id.</summary>
    int ProcessId { get; }

    /// <summary>Reports whether the process has ended, without consuming any wait state.</summary>
    bool HasExited { get; }

    /// <summary>
    /// The process's exit code once it has ended, otherwise <see langword="null"/>. Reading it never
    /// waits and never changes what a later read returns.
    /// </summary>
    int? ExitCode { get; }
}
