using System.Collections.Generic;
using SoulComputerUse.Windows.Hosting.Startup.Split;
using SoulComputerUse.Windows.Native;
using SoulComputerUse.Windows.Rdp;

namespace SoulComputerUse.IntegrationTests;

/// <summary>
/// The production session reader against the live machine. The logon gate decides from these two facts, so
/// a reader that fails silently looks exactly like an operator who never logged on; the console session
/// always holds the operator's own logon, which makes it the one case that must answer yes on any machine.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class WtsSessionSourceTests
{
    [TestMethod]
    public void TheProductionReaderSeesTheSessionsAndTheConsoleUser()
    {
        WtsSessionSource source = new();
        int consoleSessionId = WtsSessionProbe.GetActiveConsoleSessionId();

        List<int> sessionIds = [.. source.ReadSessionIds()];

        Assert.Contains(
            LeftoverChildSessionPolicy.ServiceSessionId,
            sessionIds,
            "Every machine has the service session, so a table without it means the enumeration is broken.");
        Assert.Contains(
            consoleSessionId,
            sessionIds,
            "The console session is the one session a test process runs under, so it must be listed.");
        Assert.IsTrue(
            source.HasLoggedOnUser(consoleSessionId),
            "The console session always holds the operator's logon, so a false here means the production query cannot read a user at all.");
    }

    [TestMethod]
    public void ATrackerThatSnapshotsTheMachineFindsNothingNewOnASecondLook()
    {
        ChildSessionLogonTracker tracker = ChildSessionLogonTracker.Start(
            new WtsSessionSource(),
            WtsSessionProbe.GetActiveConsoleSessionId());

        Assert.IsFalse(
            tracker.HasCreatedSession(),
            "No connection was opened, so no session of this run can exist yet.");
        Assert.AreEqual(0, tracker.CreatedSessionId);
        Assert.IsFalse(tracker.HasLoggedOnUser());
    }
}