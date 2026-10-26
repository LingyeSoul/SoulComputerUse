using System;

namespace SoulComputerUse.Windows.Rdp;

/// <summary>A credential prompt setting on the remote desktop control.</summary>
public enum RdpCredentialPromptSetting
{
    /// <summary>Ask the user for credentials instead of reusing the signed-in identity.</summary>
    PromptForCredentials = 0,

    /// <summary>Allow the control to prompt at all.</summary>
    AllowPromptingForCredentials = 1,
}

/// <summary>
/// Applies the credential prompt settings that make the remote desktop control ask the operator for
/// credentials in its own Windows dialog.
/// </summary>
/// <remarks>
/// <para>
/// These two members live on the non-scriptable interfaces
/// (<c>IMsRdpClientNonScriptable3</c> / <c>IMsRdpClientNonScriptable5</c>), not on the scriptable one,
/// so they are not reachable through <c>IDispatch</c> either.
/// </para>
/// <para>
/// Whether they are reachable at all on this machine was measured on 2026-10-26 by the standalone
/// probe <c>SoulComputerUse.RdpCredentialExperiment</c>, which creates the control through the raw
/// <c>IUnknown</c> route and asks it directly:
/// <c>QueryInterface(IUnknown)</c> answered success, while
/// <c>IMsRdpExtendedSettings</c>, <c>IMsRdpClientNonScriptable3</c>,
/// <c>IMsRdpClientNonScriptable5</c> and the prompt-enabled
/// <c>IMsRdpClientNonScriptable3</c> variant all answered
/// <c>E_NOINTERFACE</c>. The registered control (RDP Client Control version 7) therefore does not
/// expose the interfaces these settings live on, so they cannot be written on this machine — not
/// because a vtable slot was wrong, but because there is no vtable to reach.
/// </para>
/// <para>
/// Consequence: the 'system-credential' gate tier does not need these settings at all. Its gate is the
/// child session's own Windows logon screen, which appears inside the session window and is completed
/// by the operator, so the application still never sees a credential. The measured
/// <c>E_NOINTERFACE</c> answers are background only: they rule out one implementation route, not the
/// gate. A stronger gate is reached by enrolling Windows Hello, which makes the 'hello-enrolled' tier
/// apply without any code change.
/// </para>
/// <para>
/// Asking for these settings must still fail loudly rather than fall back to a silent credential-less
/// connection, so that a future control that does expose them cannot slip through unverified.
/// </para>
/// </remarks>
public static class RdpCredentialPromptSettings
{
    /// <summary>The property name used by the extended-settings route, for the read-back check.</summary>
    public static string ToExtendedSettingName(RdpCredentialPromptSetting setting)
    {
        return setting switch
        {
            RdpCredentialPromptSetting.PromptForCredentials => "PromptForCredentials",
            RdpCredentialPromptSetting.AllowPromptingForCredentials => "AllowPromptingForCredentials",
            _ => throw new ArgumentOutOfRangeException(nameof(setting), setting, "Unknown credential prompt setting."),
        };
    }

    /// <summary>
    /// Applies both settings and verifies each by reading it back. Any failure is thrown: a control
    /// that would not ask for credentials must never be connected with, because the session would
    /// then silently run with the signed-in identity.
    /// </summary>
    public static void ApplyAndVerify(object ocx)
    {
        ArgumentNullException.ThrowIfNull(ocx);

        foreach (RdpCredentialPromptSetting setting in Enum.GetValues<RdpCredentialPromptSetting>())
        {
            RdpExtendedSettings.SetBooleanAndVerify(ocx, ToExtendedSettingName(setting), true);
        }
    }

    /// <summary>
    /// The error raised when the credential prompt settings cannot be reached at all. The message
    /// names the interfaces involved so the next implementer does not hunt through <c>IDispatch</c>.
    /// </summary>
    public static RdpSurfaceException Unreachable(RdpCredentialPromptSetting setting)
    {
        return new RdpSurfaceException(
            "QueryInterface",
            $"member={ToExtendedSettingName(setting)}",
            0,
            "the credential prompt setting is not reachable through IDispatch; it lives on IMsRdpClientNonScriptable3 (PromptForCredentials) and IMsRdpClientNonScriptable5 (AllowPromptingForCredentials), so the connection is refused instead of running without a credential prompt");
    }
}