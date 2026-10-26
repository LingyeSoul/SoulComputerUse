using System;
using System.Collections.Generic;
using SoulComputerUse.Core.Session;

namespace SoulComputerUse.Windows.Hosting.Startup;

/// <summary>
/// The exception a refused or failed start raises: the contract code, the last stage that ran, and the
/// stage results collected so far, so a caller can report what was cleaned up without parsing text.
/// </summary>
/// <remarks>
/// The message is a fixed template plus the code's wire name and the stage name; it never carries a path,
/// an account, a SID or a nonce. The stage results are exposed both as a property and under
/// <see cref="Data"/> so a caller that only logs the exception still sees how far the start got.
/// </remarks>
public sealed class SessionStartException : Exception
{
    /// <summary>The <see cref="Exception.Data"/> key under which the stage results travel.</summary>
    public const string StepsDataKey = "SoulComputerUse.Hosting.Startup.SessionStartSteps";

    /// <summary>Creates the exception for one refused or failed start.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The code is not a defined value.</exception>
    /// <exception cref="ArgumentNullException">The step list is null.</exception>
    public SessionStartException(
        SessionErrorCode code,
        string message,
        IReadOnlyList<SessionStartStepResult> steps)
        : this(code, message, steps, null)
    {
    }

    /// <summary>Creates the exception around the failure that caused the refusal.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The code is not a defined value.</exception>
    /// <exception cref="ArgumentNullException">The step list is null.</exception>
    public SessionStartException(
        SessionErrorCode code,
        string message,
        IReadOnlyList<SessionStartStepResult> steps,
        Exception? innerException)
        : base(message, innerException)
    {
        if (!Enum.IsDefined(code))
        {
            throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown session error code.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentNullException.ThrowIfNull(steps);

        SessionStartStepResult[] copy = new SessionStartStepResult[steps.Count];
        for (int index = 0; index < steps.Count; index++)
        {
            copy[index] = steps[index]
                ?? throw new ArgumentException("A startup exception cannot carry a null step result.", nameof(steps));
        }

        Code = code;
        Steps = Array.AsReadOnly(copy);
        Data[StepsDataKey] = Steps;
    }

    /// <summary>The contract error code of the refusal.</summary>
    public SessionErrorCode Code { get; }

    /// <summary>One result per stage this attempt executed or explicitly skipped, in pipeline order.</summary>
    public IReadOnlyList<SessionStartStepResult> Steps { get; }
}
