using System;
using System.Linq;
using SoulComputerUse.Windows.Rdp;

namespace SoulComputerUse.ContractTests;

/// <summary>
/// The connection guard is fail-closed: without an explicit child-session intent nothing connects,
/// local or not. Recognising "this machine" by name is deliberately not what allows a connection,
/// because a raw local IP or an alias resolving here would slip past any name list.
/// </summary>
[TestClass]
public sealed class RdpConnectionGuardTests
{
    [TestMethod]
    public void WithoutChildSessionIntentEvenARemoteLookingServerIsRefused()
    {
        InvalidOperationException error = Assert.ThrowsExactly<InvalidOperationException>(
            () => RdpConnectionGuard.EnsureChildSessionIntent(connectToChildSession: false, "desktop.example.com"));

        StringAssert.Contains(error.Message, "console session", "The refusal must explain what would happen.");
    }

    [TestMethod]
    public void WithoutChildSessionIntentALocalIpIsRefusedToo()
    {
        Assert.ThrowsExactly<InvalidOperationException>(
            () => RdpConnectionGuard.EnsureChildSessionIntent(connectToChildSession: false, "192.0.2.10"),
            "A raw local IP is exactly the spelling a name list would miss.");
    }

    [TestMethod]
    public void WithChildSessionIntentTheConnectionIsAllowed()
    {
        RdpConnectionGuard.EnsureChildSessionIntent(connectToChildSession: true, "localhost");
    }

    [TestMethod]
    public void LocalRecognitionIsForDiagnosticsOnly()
    {
        Assert.IsTrue(RdpServerDescription.IsRecognisablyLocal("localhost"));
        Assert.IsTrue(RdpServerDescription.IsRecognisablyLocal("LOCALHOST"));
        Assert.IsTrue(RdpServerDescription.IsRecognisablyLocal("127.0.0.1"));
        Assert.IsTrue(RdpServerDescription.IsRecognisablyLocal("::1"));
        Assert.IsTrue(RdpServerDescription.IsRecognisablyLocal(Environment.MachineName));
        Assert.IsTrue(RdpServerDescription.IsRecognisablyLocal(Environment.MachineName + ".example.test"));

        // "Not recognised" must never be read as "remote": the guard does not consult this at all.
        Assert.IsFalse(RdpServerDescription.IsRecognisablyLocal("desktop.example.com"));
        Assert.IsFalse(RdpServerDescription.IsRecognisablyLocal("192.0.2.10"));
    }

    [TestMethod]
    public void LocalRecognitionRefusesBlankInput()
    {
        Assert.ThrowsExactly<ArgumentException>(() => RdpServerDescription.IsRecognisablyLocal("   "));
    }
}

/// <summary>
/// The connection settings are validated before anything is connected, and their log rendering never
/// contains credential material because there is none to contain.
/// </summary>
[TestClass]
public sealed class RdpConnectionSettingsTests
{
    [TestMethod]
    public void ValidSettingsPassValidation()
    {
        RdpConnectionSettings settings = new("desktop.example.com", 1920, 1080, ConnectToChildSession: true);

        settings.Validate();
        StringAssert.Contains(settings.Describe(), "1920x1080");
        StringAssert.Contains(settings.Describe(), "childSession=True");
    }

    [TestMethod]
    public void EmptyServerIsRefused()
    {
        RdpConnectionSettings settings = new("", 1920, 1080, ConnectToChildSession: true);

        Assert.ThrowsExactly<ArgumentException>(settings.Validate);
    }

    [TestMethod]
    public void NonPositiveDesktopSizeIsRefused()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            new RdpConnectionSettings("desktop.example.com", 0, 1080, ConnectToChildSession: true).Validate);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            new RdpConnectionSettings("desktop.example.com", 1920, 0, ConnectToChildSession: true).Validate);
    }

    [TestMethod]
    public void TheTypeCarriesNoCredentialFields()
    {
        string[] propertyNames = typeof(RdpConnectionSettings)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        string[] forbidden = ["Password", "Pin", "Secret", "Credential", "Token"];

        foreach (string propertyName in propertyNames)
        {
            foreach (string forbiddenPart in forbidden)
            {
                Assert.IsFalse(
                    propertyName.Contains(forbiddenPart, StringComparison.OrdinalIgnoreCase),
                    $"The connection settings must never carry '{forbiddenPart}'; the operator's credential stays inside the Windows dialog.");
            }
        }
    }
}