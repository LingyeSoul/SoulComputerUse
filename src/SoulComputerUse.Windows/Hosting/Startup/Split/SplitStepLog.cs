using System;
using System.Collections.Generic;
using SoulComputerUse.Core.Session;

namespace SoulComputerUse.Windows.Hosting.Startup.Split;

/// <summary>
/// The stage outcomes one split segment has produced so far, published as an immutable snapshot.
/// </summary>
/// <remarks>
/// The snapshot is replaced instead of wrapped, so a caller that reads it during a run can never watch
/// the list change under it, and a caller that reads it after a failure sees exactly the stages that ran.
/// The segment appends in contract order; <see cref="SessionStartStepResult"/> already refuses a result
/// that contradicts itself.
/// </remarks>
internal sealed class SplitStepLog
{
    private readonly List<SessionStartStepResult> _steps = [];
    private IReadOnlyList<SessionStartStepResult> _snapshot = Array.AsReadOnly(Array.Empty<SessionStartStepResult>());

    /// <summary>The stages recorded so far, as a snapshot that never changes afterwards.</summary>
    internal IReadOnlyList<SessionStartStepResult> Snapshot => _snapshot;

    /// <summary>Records one stage whose effect is in place.</summary>
    internal void Succeeded(SessionStartStage stage)
    {
        Append(new SessionStartStepResult(stage, executed: true, succeeded: true, errorCode: null));
    }

    /// <summary>Records one stage that ran and refused with a contract code.</summary>
    internal void Failed(SessionStartStage stage, SessionErrorCode code)
    {
        Append(new SessionStartStepResult(stage, executed: true, succeeded: false, code));
    }

    private void Append(SessionStartStepResult result)
    {
        _steps.Add(result);
        _snapshot = Array.AsReadOnly(_steps.ToArray());
    }
}
