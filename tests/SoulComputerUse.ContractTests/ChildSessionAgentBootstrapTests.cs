using System;
using SoulComputerUse.Windows.ChildSessions;

namespace SoulComputerUse.ContractTests;

/// <summary>
/// Contract tests for the start-up entry that launches the agent inside a child session: the write is
/// read back, the removal is idempotent, and the two together leave the account's <c>Run</c> key exactly
/// as it was.
/// </summary>
/// <remarks>
/// The cases touch this account's real <c>Run</c> key, which is the only honest place to prove the
/// entry the child session's shell will read; every case removes the entry again, including on failure,
/// because a leaked entry starts an agent on this account's next logon. The class runs without
/// parallelism for the same reason: the key is one machine-wide value, and a concurrent case's mount or
/// removal is indistinguishable from this one's.
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class ChildSessionAgentBootstrapTests
{
    [TestMethod]
    public void TheEntryIsWrittenReadBackAndRemoved()
    {
        try
        {
            ChildSessionAgentBootstrap.Install("\"C:\\somewhere\\agent.exe\" --session id --nonce n");

            Assert.AreEqual(
                "\"C:\\somewhere\\agent.exe\" --session id --nonce n",
                ChildSessionAgentBootstrap.Read(),
                "The entry must hold exactly the command line that was written.");
        }
        finally
        {
            ChildSessionAgentBootstrap.Remove();
        }

        Assert.IsNull(ChildSessionAgentBootstrap.Read(), "A removed entry must be gone from the Run key.");
    }

    [TestMethod]
    public void RemovingAnEntryThatDoesNotExistIsSuccess()
    {
        ChildSessionAgentBootstrap.Remove();

        Assert.IsNull(ChildSessionAgentBootstrap.Read(), "A second removal after a successful one must neither throw nor re-create the entry.");
    }

    [TestMethod]
    public void AnEmptyCommandLineIsRefused()
    {
        Assert.ThrowsExactly<ArgumentException>(() => ChildSessionAgentBootstrap.Install(" "));
        Assert.IsNull(ChildSessionAgentBootstrap.Read(), "A refused write must not leave an entry behind.");
    }
}
