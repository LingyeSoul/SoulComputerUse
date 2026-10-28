using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Desktop;
using SoulComputerUse.Windows.Shell;

namespace SoulComputerUse.IntegrationTests;

/// <summary>
/// Covers the blocked-state gate of sections 1.1 and 1.3: the pure desktop verdict, the guard that
/// turns a blocked verdict into <c>E_BLOCKED</c>, and the decorator that keeps every capture and
/// input call away from the inner surface while the desktop cannot receive it.
/// </summary>
/// <remarks>
/// No test locks the workstation or raises a UAC prompt. The pure decision is asserted over the
/// facts alone, the decorator is driven through a recording surface, and the one real probe case only
/// requires that the probe answers with a defined verdict whatever this machine's state is.
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class DesktopBlockedStateTests
{
    private const string InteractiveStation = "WinSta0";

    /// <summary>An active session on the interactive default desktop is usable.</summary>
    [TestMethod]
    public void EvaluateReturnsUsableForAnActiveSessionOnTheDefaultDesktop()
    {
        DesktopAvailabilityVerdict verdict = DesktopAvailability.Evaluate(
            true,
            DesktopSessionConnectState.Active,
            "Default",
            InteractiveStation);

        Assert.AreEqual(DesktopAvailabilityVerdict.Usable, verdict);
    }

    /// <summary>
    /// A session Windows does not report as active is refused even though its desktop still carries
    /// the interactive name and can still be opened.
    /// </summary>
    [TestMethod]
    public void EvaluateReturnsNonInteractiveWhenTheSessionIsNotActive()
    {
        DesktopAvailabilityVerdict verdict = DesktopAvailability.Evaluate(
            true,
            DesktopSessionConnectState.Other,
            "Default",
            InteractiveStation);

        Assert.AreEqual(DesktopAvailabilityVerdict.BlockedNonInteractive, verdict);
    }

    /// <summary>
    /// A session whose client is gone is reported as disconnected, not blocked: Windows keeps its
    /// desktop name and even lets it open, so only the connection state separates a dead session from
    /// a usable one. The contract has no automatic reconnect, which is what makes this terminal.
    /// </summary>
    [TestMethod]
    public void EvaluateReturnsDisconnectedWhenTheSessionHasNoAttachedClient()
    {
        DesktopAvailabilityVerdict verdict = DesktopAvailability.Evaluate(
            true,
            DesktopSessionConnectState.Disconnected,
            "Default",
            InteractiveStation);

        Assert.AreEqual(
            DesktopAvailabilityVerdict.Disconnected,
            verdict,
            "A disconnected session can still show a Default desktop; the connection state must decide first.");
    }

    /// <summary>A refused input-desktop handle is reported as a locked desktop.</summary>
    [TestMethod]
    public void EvaluateReturnsLockedDesktopWhenTheInputDesktopCannotBeOpened()
    {
        DesktopAvailabilityVerdict verdict = DesktopAvailability.Evaluate(
            false,
            DesktopSessionConnectState.Active,
            null,
            InteractiveStation);

        Assert.AreEqual(DesktopAvailabilityVerdict.BlockedLockedDesktop, verdict);
    }

    /// <summary>An opened desktop named <c>Winlogon</c> is the secure desktop of a UAC or logon prompt.</summary>
    [TestMethod]
    public void EvaluateReturnsSecureDesktopForTheWinlogonDesktop()
    {
        DesktopAvailabilityVerdict verdict = DesktopAvailability.Evaluate(
            true,
            DesktopSessionConnectState.Active,
            "Winlogon",
            InteractiveStation);

        Assert.AreEqual(DesktopAvailabilityVerdict.BlockedSecureDesktop, verdict);
    }

    /// <summary>
    /// Any other desktop name is refused as secure rather than guessed usable: only <c>Default</c> is
    /// the desktop the operator sees.
    /// </summary>
    [TestMethod]
    public void EvaluateReturnsSecureDesktopForAnyOtherDesktopName()
    {
        DesktopAvailabilityVerdict verdict = DesktopAvailability.Evaluate(
            true,
            DesktopSessionConnectState.Active,
            "ScreenSaver",
            InteractiveStation);

        Assert.AreEqual(DesktopAvailabilityVerdict.BlockedSecureDesktop, verdict);
    }

    /// <summary>
    /// Order rule: a failed open outranks the name check, so a locked desktop is not reported as the
    /// secure desktop merely because the caller still holds a stale <c>Winlogon</c> name.
    /// </summary>
    [TestMethod]
    public void EvaluateReportsALockedDesktopBeforeTheWinlogonName()
    {
        DesktopAvailabilityVerdict verdict = DesktopAvailability.Evaluate(
            false,
            DesktopSessionConnectState.Active,
            "Winlogon",
            InteractiveStation);

        Assert.AreEqual(
            DesktopAvailabilityVerdict.BlockedLockedDesktop,
            verdict,
            "The failed open must decide before any desktop name is looked at.");
    }

    /// <summary>
    /// Order rule: the session gate outranks the handle check, so a disconnected session is not
    /// reported as a locked desktop.
    /// </summary>
    [TestMethod]
    public void EvaluateReportsANonInteractiveSessionBeforeALockedDesktop()
    {
        DesktopAvailabilityVerdict verdict = DesktopAvailability.Evaluate(
            false,
            DesktopSessionConnectState.Other,
            null,
            InteractiveStation);

        Assert.AreEqual(
            DesktopAvailabilityVerdict.BlockedNonInteractive,
            verdict,
            "The session state must decide before the input desktop is considered.");
    }

    /// <summary>
    /// A process that does not run on the interactive window station cannot reach the operator's
    /// desktop, so its session is treated as non-interactive.
    /// </summary>
    [TestMethod]
    public void EvaluateTreatsANonInteractiveWindowStationAsNonInteractive()
    {
        DesktopAvailabilityVerdict verdict = DesktopAvailability.Evaluate(
            true,
            DesktopSessionConnectState.Active,
            "Default",
            "Service-0x0-3e7$");

        Assert.AreEqual(DesktopAvailabilityVerdict.BlockedNonInteractive, verdict);
    }

    /// <summary>An unreadable station name is an error rather than a silent usable verdict.</summary>
    [TestMethod]
    public void EvaluateRejectsAnEmptyWindowStationName()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => DesktopAvailability.Evaluate(true, DesktopSessionConnectState.Active, "Default", string.Empty));
    }

    /// <summary>Every blocked verdict maps onto one of the caption's blocked causes.</summary>
    [TestMethod]
    public void EveryBlockedVerdictMapsOntoACaptionCause()
    {
        Assert.AreEqual(
            BlockedCause.ProtectedDesktop,
            DesktopBlockReason.ToBlockedCause(DesktopAvailabilityVerdict.BlockedLockedDesktop));
        Assert.AreEqual(
            BlockedCause.ProtectedDesktop,
            DesktopBlockReason.ToBlockedCause(DesktopAvailabilityVerdict.BlockedSecureDesktop));
        Assert.AreEqual(
            BlockedCause.NonInteractiveSession,
            DesktopBlockReason.ToBlockedCause(DesktopAvailabilityVerdict.BlockedNonInteractive));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => DesktopBlockReason.ToBlockedCause(DesktopAvailabilityVerdict.Usable));
    }

    /// <summary>
    /// Only a blocked verdict maps onto <c>E_BLOCKED</c>. A disconnected verdict is the contract's
    /// terminal state, so it reports <c>E_DISCONNECTED</c> and has no blocked cause.
    /// </summary>
    [TestMethod]
    public void OnlyBlockedVerdictsMapOntoTheBlockedErrorCode()
    {
        Assert.AreEqual(
            SessionErrorCode.Blocked,
            DesktopBlockReason.ToErrorCode(DesktopAvailabilityVerdict.BlockedLockedDesktop));
        Assert.AreEqual(
            SessionErrorCode.Blocked,
            DesktopBlockReason.ToErrorCode(DesktopAvailabilityVerdict.BlockedSecureDesktop));
        Assert.AreEqual(
            SessionErrorCode.Blocked,
            DesktopBlockReason.ToErrorCode(DesktopAvailabilityVerdict.BlockedNonInteractive));
        Assert.AreEqual(
            SessionErrorCode.Disconnected,
            DesktopBlockReason.ToErrorCode(DesktopAvailabilityVerdict.Disconnected));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => DesktopBlockReason.ToErrorCode(DesktopAvailabilityVerdict.Usable));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => DesktopBlockReason.ToBlockedCause(DesktopAvailabilityVerdict.Disconnected));
    }

    /// <summary>
    /// Five input operations are refused with <c>E_BLOCKED</c> while the desktop is blocked, and the
    /// inner surface is never reached.
    /// </summary>
    [TestMethod]
    public void GuardedSurfaceRefusesEveryInputOperationWhileBlocked()
    {
        RecordingDesktopSurface inner = new();
        BlockedGuard guard = BlockedGuardFor(new DesktopBlockObservation(DesktopSessionConnectState.Active, InteractiveStation, false, null));
        GuardedDesktopSurface surface = new(inner, guard);

        Action[] operations =
        [
            () => surface.MovePointer(1, 1),
            () => surface.Click(1, 1, MouseButton.Left, 1),
            () => surface.Scroll(1, 1, 0, 120),
            () => surface.TypeText("text"),
            () => surface.PressKeys(new SessionKey[] { SessionKey.Enter }),
        ];

        for (int index = 0; index < operations.Length; index++)
        {
            SessionOperationException error = Assert.ThrowsExactly<SessionOperationException>(
                operations[index],
                $"Operation {index} must be refused while the desktop is blocked.");

            Assert.AreEqual(SessionErrorCode.Blocked, error.Code, $"Operation {index} must report the blocked code.");
            Assert.AreEqual("E_BLOCKED", SessionErrorCodeNames.ToWireName(error.Code));
            Assert.IsFalse(error.Retryable, $"Operation {index} must not be retryable until the operator acts.");
        }

        Assert.AreEqual(
            0,
            inner.CallCount,
            "A refused call must never reach the inner surface, or input would land on a protected desktop.");
    }

    /// <summary>
    /// Input on a session whose client is gone is refused with <c>E_DISCONNECTED</c>: the guard must
    /// not report a terminal session as the blocked state, which the contract lets the operator
    /// recover by acting at the machine.
    /// </summary>
    [TestMethod]
    public void GuardedSurfaceReportsTheDisconnectedCodeWhenTheSessionHasNoClient()
    {
        RecordingDesktopSurface inner = new();
        BlockedGuard guard = BlockedGuardFor(new DesktopBlockObservation(
            DesktopSessionConnectState.Disconnected,
            InteractiveStation,
            true,
            "Default"));
        GuardedDesktopSurface surface = new(inner, guard);

        SessionOperationException error = Assert.ThrowsExactly<SessionOperationException>(
            () => surface.MovePointer(1, 1),
            "A session with no attached client must refuse input.");

        Assert.AreEqual(SessionErrorCode.Disconnected, error.Code);
        Assert.AreEqual("E_DISCONNECTED", SessionErrorCodeNames.ToWireName(error.Code));
        Assert.IsFalse(error.Retryable);
        Assert.AreEqual(0, inner.CallCount, "A refused call must never reach the inner surface.");
    }

    /// <summary>
    /// The screenshot stays available while blocked: section 1.3 keeps the read-only tools working so
    /// the operator can judge the prompt from the frame.
    /// </summary>
    [TestMethod]
    public void GuardedSurfaceStillCapturesWhileBlocked()
    {
        RecordingDesktopSurface inner = new();
        BlockedGuard guard = BlockedGuardFor(new DesktopBlockObservation(DesktopSessionConnectState.Active, InteractiveStation, false, null));
        GuardedDesktopSurface surface = new(inner, guard);

        byte[] frame = surface.CapturePng();

        CollectionAssert.AreEqual(inner.Frame, frame);
        Assert.AreEqual(
            1,
            inner.CallCount,
            "The screenshot must reach the inner surface in the blocked state, because the operator needs the frame.");
    }

    /// <summary>All six operations are forwarded exactly once when the desktop is usable.</summary>
    [TestMethod]
    public void GuardedSurfaceForwardsEveryOperationWhileUsable()
    {
        RecordingDesktopSurface inner = new();
        BlockedGuard guard = BlockedGuardFor(new DesktopBlockObservation(DesktopSessionConnectState.Active, InteractiveStation, true, "Default"));
        GuardedDesktopSurface surface = new(inner, guard);
        SessionKey[] keys = [SessionKey.Ctrl, SessionKey.Esc];

        byte[] frame = surface.CapturePng();
        surface.MovePointer(3, 4);
        surface.Click(5, 6, MouseButton.Right, 2);
        surface.Scroll(7, 8, 9, 10);
        surface.TypeText("hello");
        surface.PressKeys(keys);

        CollectionAssert.AreEqual(inner.Frame, frame);
        Assert.AreEqual((3, 4), inner.LastMove);
        Assert.AreEqual((5, 6, MouseButton.Right, 2), inner.LastClick);
        Assert.AreEqual((7, 8, 9, 10), inner.LastScroll);
        Assert.AreEqual("hello", inner.LastText);
        Assert.HasCount(keys.Length, inner.LastKeys);
        for (int index = 0; index < keys.Length; index++)
        {
            Assert.AreEqual(keys[index], inner.LastKeys[index], $"Key {index} must be forwarded in order.");
        }

        Assert.AreEqual(6, inner.CallCount, "Every operation must reach the inner surface exactly once.");
    }

    /// <summary>
    /// The bounds are readable while blocked: the status path needs the screen size, and reading it
    /// touches nothing on the desktop.
    /// </summary>
    [TestMethod]
    public void GetScreenIsAvailableWhileBlocked()
    {
        RecordingDesktopSurface inner = new() { Screen = new DesktopScreen(0, 0, 1280, 720) };
        BlockedGuard guard = BlockedGuardFor(new DesktopBlockObservation(DesktopSessionConnectState.Active, InteractiveStation, false, null));
        GuardedDesktopSurface surface = new(inner, guard);

        DesktopScreen screen = surface.GetScreen();

        Assert.AreEqual(1280, screen.Width);
        Assert.AreEqual(720, screen.Height);
        Assert.AreEqual(1, inner.CallCount, "The bounds must come from the inner surface.");
    }

    /// <summary>
    /// The verdict is never cached: a call that was refused once succeeds as soon as the desktop
    /// becomes usable again, without a new guard.
    /// </summary>
    [TestMethod]
    public void GuardedSurfaceProbesAgainForEveryCall()
    {
        RecordingDesktopSurface inner = new();
        int probes = 0;
        BlockedGuard guard = new(
            () =>
            {
                probes++;
                return probes == 1
                    ? new DesktopBlockObservation(DesktopSessionConnectState.Active, InteractiveStation, false, null)
                    : new DesktopBlockObservation(DesktopSessionConnectState.Active, InteractiveStation, true, "Default");
            });
        GuardedDesktopSurface surface = new(inner, guard);

        Assert.ThrowsExactly<SessionOperationException>(() => surface.MovePointer(1, 1));
        surface.MovePointer(1, 1);

        Assert.AreEqual(2, probes, "Every call must read the desktop again; a cached verdict would let input through after a lock.");
        Assert.AreEqual((1, 1), inner.LastMove);
        Assert.AreEqual(1, inner.CallCount);
    }

    /// <summary>
    /// The real probe answers with a verdict of the enumeration on this machine, whatever state the
    /// desktop is in. A locked or secure desktop is an allowed answer; only a throw is a failure.
    /// </summary>
    [TestMethod]
    public void RealProbeReturnsAVerdict()
    {
        DesktopBlockObservation observation = DesktopBlockProbe.Probe();

        DesktopAvailabilityVerdict verdict = DesktopAvailability.Evaluate(
            observation.InputDesktopOpenSucceeded,
            observation.SessionConnectState,
            observation.InputDesktopName,
            observation.SessionWindowStation);

        Assert.IsTrue(Enum.IsDefined(verdict), "The probe must produce a verdict of the enumeration.");
        Assert.IsFalse(
            observation.InputDesktopOpenSucceeded && string.IsNullOrEmpty(observation.InputDesktopName),
            "An opened input desktop must report its name.");
    }

    /// <summary>
    /// Each blocked message names only the condition and the way out: no drive path, no UNC path, no
    /// account name and no account identifier may appear in an error payload.
    /// </summary>
    [TestMethod]
    public void BlockedMessagesCarryNoMachineData()
    {
        DesktopAvailabilityVerdict[] blockedVerdicts =
        [
            DesktopAvailabilityVerdict.BlockedLockedDesktop,
            DesktopAvailabilityVerdict.BlockedSecureDesktop,
            DesktopAvailabilityVerdict.BlockedNonInteractive,
        ];

        string accountName = Environment.UserName;
        foreach (DesktopAvailabilityVerdict verdict in blockedVerdicts)
        {
            string message = DesktopBlockReason.Describe(verdict);

            Assert.IsNotEmpty(message, "A blocked message must say what happened.");
            Assert.DoesNotContain("C:\\", message, StringComparison.OrdinalIgnoreCase, $"{verdict} must not carry a drive path.");
            Assert.DoesNotContain("\\", message, StringComparison.Ordinal, $"{verdict} must not carry a path separator.");
            Assert.DoesNotMatchRegex(
                new Regex(@"^[A-Za-z]:", RegexOptions.CultureInvariant),
                message,
                $"{verdict} must not start with a drive letter.");
            Assert.DoesNotMatchRegex(
                new Regex(@"S-1-5-", RegexOptions.CultureInvariant),
                message,
                $"{verdict} must not carry an account identifier.");

            if (accountName.Length > 0)
            {
                Assert.DoesNotContain(
                    accountName,
                    message,
                    StringComparison.OrdinalIgnoreCase,
                    $"{verdict} must not carry the account that owns this machine session.");
            }
        }
    }

    /// <summary>
    /// The three messages differ, so an operator can tell a locked desktop from a UAC prompt, yet all
    /// three are one non-retryable <c>E_BLOCKED</c>.
    /// </summary>
    [TestMethod]
    public void BlockedMessagesDistinguishTheThreeCauses()
    {
        string locked = BlockedMessageFor(new DesktopBlockObservation(DesktopSessionConnectState.Active, InteractiveStation, false, null));
        string secure = BlockedMessageFor(new DesktopBlockObservation(DesktopSessionConnectState.Active, InteractiveStation, true, "Winlogon"));
        string nonInteractive = BlockedMessageFor(new DesktopBlockObservation(DesktopSessionConnectState.Other, InteractiveStation, true, "Default"));

        Assert.AreNotEqual(locked, secure, false, "A locked desktop and a secure desktop must be told apart.");
        Assert.AreNotEqual(locked, nonInteractive, false, "A locked desktop and a non-interactive session must be told apart.");
        Assert.AreNotEqual(secure, nonInteractive, false, "A secure desktop and a non-interactive session must be told apart.");
        StringAssert.Contains(locked, "locked");
        StringAssert.Contains(secure, "protected desktop");
        StringAssert.Contains(nonInteractive, "not interactive");
    }

    private static BlockedGuard BlockedGuardFor(DesktopBlockObservation observation)
    {
        return new BlockedGuard(() => observation);
    }

    private static string BlockedMessageFor(DesktopBlockObservation observation)
    {
        GuardedDesktopSurface surface = new(new RecordingDesktopSurface(), BlockedGuardFor(observation));
        SessionOperationException error = Assert.ThrowsExactly<SessionOperationException>(() => surface.MovePointer(1, 1));
        Assert.AreEqual(SessionErrorCode.Blocked, error.Code);
        return error.Message;
    }

    /// <summary>Records every surface call so a test can prove a refused call never reached it.</summary>
    private sealed class RecordingDesktopSurface : IDesktopSurface
    {
        internal DesktopScreen Screen { get; init; } = new(0, 0, 1, 1);

        internal byte[] Frame { get; init; } = [0x01, 0x02];

        internal int CallCount { get; private set; }

        internal (int X, int Y)? LastMove { get; private set; }

        internal (int X, int Y, MouseButton Button, int Count)? LastClick { get; private set; }

        internal (int X, int Y, int DeltaX, int DeltaY)? LastScroll { get; private set; }

        internal string? LastText { get; private set; }

        internal IReadOnlyList<SessionKey> LastKeys { get; private set; } = [];

        public DesktopScreen GetScreen()
        {
            CallCount++;
            return Screen;
        }

        public byte[] CapturePng()
        {
            CallCount++;
            return Frame;
        }

        public void MovePointer(int x, int y)
        {
            CallCount++;
            LastMove = (x, y);
        }

        public void Click(int x, int y, MouseButton button, int count)
        {
            CallCount++;
            LastClick = (x, y, button, count);
        }

        public void Scroll(int x, int y, int deltaX, int deltaY)
        {
            CallCount++;
            LastScroll = (x, y, deltaX, deltaY);
        }

        public void TypeText(string text)
        {
            CallCount++;
            LastText = text;
        }

        public void PressKeys(IReadOnlyList<SessionKey> keys)
        {
            CallCount++;
            LastKeys = [.. keys];
        }
    }
}
