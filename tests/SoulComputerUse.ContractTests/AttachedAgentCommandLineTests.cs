using System;
using SoulComputerUse.Windows.Hosting;

namespace SoulComputerUse.ContractTests;

/// <summary>
/// Contract tests for the host's attach-mode command line. The parser must not echo a rejected nonce
/// and must not accept agent-only gate values.
/// </summary>
[TestClass]
public sealed class AttachedAgentCommandLineTests
{
    private const string SessionId = "b3f1c2d4-5678-4abc-9def-0a1b2c3d4e5f";
    private const string Nonce = "0123456789ABCDEF0123456789ABCDEF";

    [TestMethod]
    public void ParseAcceptsTheSessionAndNonce()
    {
        AttachedAgentOptions options = AttachedAgentCommandLine.Parse(
            ["--attach-agent", SessionId, "--agent-nonce", Nonce]);

        Assert.AreEqual(SessionId, options.SessionId);
        Assert.AreEqual(Nonce, options.Nonce);
    }

    [TestMethod]
    public void ParseRejectsMissingUnknownAndDuplicatedArguments()
    {
        Assert.ThrowsExactly<ArgumentException>(() => AttachedAgentCommandLine.Parse([]));
        Assert.ThrowsExactly<ArgumentException>(
            () => AttachedAgentCommandLine.Parse(["--attach-agent", SessionId]));
        Assert.ThrowsExactly<ArgumentException>(
            () => AttachedAgentCommandLine.Parse(["--agent-nonce", Nonce]));
        Assert.ThrowsExactly<ArgumentException>(
            () => AttachedAgentCommandLine.Parse(["--attach-agent", SessionId, "--agent-nonce"]));
        Assert.ThrowsExactly<ArgumentException>(
            () => AttachedAgentCommandLine.Parse(
                ["--attach-agent", SessionId, "--agent-nonce", Nonce, "--auth-tier", "system-credential"]));
        Assert.ThrowsExactly<ArgumentException>(
            () => AttachedAgentCommandLine.Parse(
                ["--attach-agent", SessionId, "--attach-agent", SessionId, "--agent-nonce", Nonce]));
    }

    [TestMethod]
    public void ParseRejectsABadNonceWithoutEchoingIt()
    {
        const string SecretLookingNonce = "SECRETSECRETSECRETSECRETSECRET12";

        ArgumentException error = Assert.ThrowsExactly<ArgumentException>(
            () => AttachedAgentCommandLine.Parse(
                ["--attach-agent", SessionId, "--agent-nonce", SecretLookingNonce]));

        Assert.IsFalse(error.Message.Contains(SecretLookingNonce, StringComparison.Ordinal));
    }

    [TestMethod]
    public void ParseRejectsANonUuidSessionId()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => AttachedAgentCommandLine.Parse(["--attach-agent", "session", "--agent-nonce", Nonce]));
    }
}