using System;

namespace SoulComputerUse.Windows.Hosting.Launch;

/// <summary>
/// Answers whether the caller's token is already elevated, because an elevated start would skip the
/// operator's UAC confirmation that authorises elevation later in the session.
/// </summary>
/// <remarks>
/// Contract section 3.1.1 treats the operator's confirmation of a UAC prompt as the act that turns the
/// child-session switch on. A host started from an already elevated process would hold that authority
/// without the confirmation, so the start is refused instead of quietly dropping the step. The port
/// exists so the launcher's decision table can pin either answer without reading the real token.
/// </remarks>
public interface IElevationGuard
{
    /// <summary>Reports whether the current process runs with an elevated administrator token.</summary>
    bool IsCallerElevated();
}
