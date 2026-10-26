using System;
using System.ComponentModel;
using System.Globalization;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;

namespace SoulComputerUse.Windows.Rdp.Capture;

/// <summary>
/// Turns a refused capture decision into the exception the caller sees. The messages are fixed
/// templates that name the situation only: no window title, class name, handle, path or account ever
/// reaches a caller, and the occlusion message never says which window covers the control.
/// </summary>
internal static class ControlCaptureFailure
{
    /// <summary>
    /// The one message a covered control produces. It says that the frame cannot be trusted and what
    /// the operator can do, and it names nothing about the covering window.
    /// </summary>
    internal const string OccludedMessage =
        "The remote desktop picture is covered, so no trustworthy frame can be captured. Bring the session window to the front and retry.";

    /// <summary>
    /// Builds the failure of one refused decision.
    /// </summary>
    /// <param name="reason">The refusal the decision reported.</param>
    /// <param name="printedSucceeded">Whether the control accepted the print request.</param>
    /// <param name="printErrorCode">The Win32 code a rejected print reported, when it reported one.</param>
    /// <returns>The exception the capture throws.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The reason is not a defined value.</exception>
    internal static Exception ForRefusal(ControlCaptureRefusal reason, bool printedSucceeded, int printErrorCode)
    {
        switch (reason)
        {
            case ControlCaptureRefusal.Occluded:
                // A covered control is the operator's to clear, so retrying the same call immediately
                // cannot succeed and the contract reports it as not retryable.
                return new SessionOperationException(SessionErrorCode.AgentError, OccludedMessage, false);
            case ControlCaptureRefusal.PrintWindowBlankAndScreenDisabled:
                return printedSucceeded
                    ? new InvalidOperationException(
                        "The control painted no visible content and the screen-region fallback is disabled, so no trustworthy frame can be produced.")
                    : new Win32Exception(
                        printErrorCode,
                        "PrintWindow failed with Win32 error "
                        + printErrorCode.ToString(CultureInfo.InvariantCulture)
                        + " and the screen-region fallback is disabled, so no frame can be produced.");
            default:
                throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown capture refusal reason.");
        }
    }
}
