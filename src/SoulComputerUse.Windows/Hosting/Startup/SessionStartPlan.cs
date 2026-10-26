using System;
using SoulComputerUse.Core.Session;

namespace SoulComputerUse.Windows.Hosting.Startup;

/// <summary>
/// One immutable startup decision: what kind of start this is, the contract code that refuses it, the
/// fixed explanation an operator sees, and the next stage of the pipeline.
/// </summary>
/// <remarks>
/// The constructor refuses a combination that could silently start a session without a gate, so a plan
/// that exists always says either "refused with a code" or "go on with these stages left".
/// </remarks>
public sealed record SessionStartPlan
{
    /// <summary>Builds one decision and refuses contradictions.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The kind or the error code is not a defined value.</exception>
    /// <exception cref="ArgumentException">The kind, the code and the next step do not belong together.</exception>
    public SessionStartPlan(
        SessionStartKind kind,
        SessionErrorCode? errorCode,
        string reason,
        SessionStartStep nextStep)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown startup decision kind.");
        }

        if (errorCode is SessionErrorCode code && !Enum.IsDefined(code))
        {
            throw new ArgumentOutOfRangeException(nameof(errorCode), code, "Unknown session error code.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (!SessionStartSteps.IsDefined(nextStep))
        {
            throw new ArgumentOutOfRangeException(nameof(nextStep), nextStep, "Unknown startup step.");
        }

        if (kind == SessionStartKind.Refuse)
        {
            if (errorCode is null)
            {
                throw new ArgumentException("A refusal must carry a contract error code.", nameof(errorCode));
            }

            if (nextStep != SessionStartStep.None)
            {
                throw new ArgumentException("A refusal must not ask for a further step.", nameof(nextStep));
            }
        }
        else
        {
            if (errorCode is not null)
            {
                throw new ArgumentException("A non-refusal decision must not carry an error code.", nameof(errorCode));
            }

            if (nextStep == SessionStartStep.None)
            {
                throw new ArgumentException("A non-refusal decision must name the next step.", nameof(nextStep));
            }
        }

        Kind = kind;
        ErrorCode = errorCode;
        Reason = reason;
        NextStep = nextStep;
    }

    /// <summary>The decision itself.</summary>
    public SessionStartKind Kind { get; }

    /// <summary>The section 2 code for a refusal, or null when the session may go on.</summary>
    public SessionErrorCode? ErrorCode { get; }

    /// <summary>A fixed explanation built from templates plus enumerations and counts only.</summary>
    public string Reason { get; }

    /// <summary>The stage this plan asks for next, or <see cref="SessionStartStep.None"/> when refused.</summary>
    public SessionStartStep NextStep { get; }

    /// <summary>Whether the session may go on after this decision.</summary>
    public bool AllowsStart => Kind != SessionStartKind.Refuse;

    /// <summary>
    /// True when a leftover ownership record meets the takeover criteria. The runner deletes that
    /// record before it writes the replacement.
    /// </summary>
    public bool DeletesStaleRecord => Kind == SessionStartKind.RecoverAndProceed;
}
