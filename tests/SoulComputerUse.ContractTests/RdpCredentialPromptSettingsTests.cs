using System;
using System.IO;
using SoulComputerUse.Windows.Rdp;

namespace SoulComputerUse.ContractTests;

/// <summary>
/// The credential prompt settings. The contract asks for the operator's credential to be collected by
/// the Windows dialog and by nothing else, so a control that cannot be told to prompt must refuse the
/// connection rather than connect with the signed-in identity.
/// </summary>
[TestClass]
public sealed class RdpCredentialPromptSettingsTests
{
    [TestMethod]
    public void SettingNamesMatchTheControlMembers()
    {
        Assert.AreEqual("PromptForCredentials", RdpCredentialPromptSettings.ToExtendedSettingName(RdpCredentialPromptSetting.PromptForCredentials));
        Assert.AreEqual("AllowPromptingForCredentials", RdpCredentialPromptSettings.ToExtendedSettingName(RdpCredentialPromptSetting.AllowPromptingForCredentials));
    }

    [TestMethod]
    public void UnreachableExplainsTheInterfacesInsteadOfBlame()
    {
        RdpSurfaceException error = RdpCredentialPromptSettings.Unreachable(RdpCredentialPromptSetting.PromptForCredentials);

        StringAssert.Contains(
            error.Message,
            "IMsRdpClientNonScriptable3",
            "The message must name the real interface, because the prototype already proved IDispatch does not expose it.");
        StringAssert.Contains(error.Message, "refused", "A control that cannot prompt must refuse the connection.");
    }

    [TestMethod]
    public void ApplyingToNothingIsRefusedInsteadOfSilentlySkipped()
    {
        Assert.ThrowsExactly<ArgumentNullException>(
            () => RdpCredentialPromptSettings.ApplyAndVerify(null!));
    }

    [TestMethod]
    public void AnObjectWithoutTheSettingsCannotQuietlyPassVerification()
    {
        // Any object that is not the real control must fail loudly. A silent pass here would mean a
        // session could start without ever being asked for credentials.
        Assert.ThrowsExactly<RdpSurfaceException>(
            () => RdpCredentialPromptSettings.ApplyAndVerify(new object()));
    }
}