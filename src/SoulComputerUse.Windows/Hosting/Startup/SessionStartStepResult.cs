using System;
using SoulComputerUse.Core.Session;

namespace SoulComputerUse.Windows.Hosting.Startup;

/// <summary>One stage outcome of a startup attempt.</summary>
public sealed record SessionStartStepResult
{
    /// <summary>Creates one stage outcome and rejects a result that contradicts itself.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The stage or the code is not a defined value.</exception>
    /// <exception cref="ArgumentException">
    /// The success flag contradicts the code, or a stage that never ran is reported as failed.
    /// </exception>
    public SessionStartStepResult(
        SessionStartStage stage,
        bool executed,
        bool succeeded,
        SessionErrorCode? errorCode)
    {
        if (!Enum.IsDefined(stage))
        {
            throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unknown startup stage.");
        }

        if (errorCode is SessionErrorCode code && !Enum.IsDefined(code))
        {
            throw new ArgumentOutOfRangeException(nameof(errorCode), code, "Unknown session error code.");
        }

        if (succeeded != (errorCode is null))
        {
            throw new ArgumentException(
                "A startup stage succeeds exactly when it carries no error code.",
                nameof(errorCode));
        }

        if (!succeeded && !executed)
        {
            throw new ArgumentException(
                "A startup stage that was not executed cannot have failed.",
                nameof(executed));
        }

        Stage = stage;
        Executed = executed;
        Succeeded = succeeded;
        ErrorCode = errorCode;
    }

    /// <summary>The pipeline stage this result belongs to.</summary>
    public SessionStartStage Stage { get; }

    /// <summary>
    /// Whether this attempt performed the stage. False only for a stage the plan skipped, which the
    /// pipeline of this layer never does: every reported stage ran.
    /// </summary>
    public bool Executed { get; }

    /// <summary>Whether the stage's effect is in place.</summary>
    public bool Succeeded { get; }

    /// <summary>The contract error code when the stage failed; null otherwise.</summary>
    public SessionErrorCode? ErrorCode { get; }
}
