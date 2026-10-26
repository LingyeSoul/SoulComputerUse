using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using SoulComputerUse.Windows.SingleInstance;

namespace SoulComputerUse.IntegrationTests;

/// <summary>
/// The single-instance lock against the real Windows kernel object. Every behavioural case uses a
/// per-run lock name, so a leftover holder from an earlier run — or a real session on this machine —
/// can never make these cases fail. Only the name test touches the contract-pinned production name,
/// and it asserts the string without creating anything.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SingleInstanceLockTests
{
    // Short on purpose: a helper that outlives its case holds a lock handle, and a leaked one makes
    // later cases report "held elsewhere" through no fault of the product.
    private const int HolderHoldMilliseconds = 3000;

    private string _lockName = string.Empty;

    private static string TestSupportPath =>
        Path.Combine(AppContext.BaseDirectory, "SoulComputerUse.TestSupport.exe");

    [TestInitialize]
    public void CreateIsolatedLockName()
    {
        // Unique per test method: no state can leak between cases, and the production name is never
        // touched by a behavioural case.
        _lockName = @"Global\SoulComputerUse-Test-" + Guid.NewGuid().ToString("N");
    }

    [TestCleanup]
    public void AssertNoLeftoverHolder()
    {
        // Only this case's own lock matters here: other test classes run their own helpers, and a
        // process-wide check would fail on their in-flight helpers rather than on a real leak.
        Assert.IsFalse(
            SingleInstanceLock.IsHeldByAnyone(_lockName),
            "The test's own lock object must be gone after the case, otherwise the case leaked a holder.");
    }

    [TestMethod]
    public void ProductionMutexNameIsTheContractPinnedGlobalGuid()
    {
        const string Prefix = @"Global\SoulComputerUse-";

        Assert.StartsWith(Prefix, SingleInstanceLock.MutexName, "The production lock must live in the Global namespace so it spans Windows accounts.");
        string suffix = SingleInstanceLock.MutexName[Prefix.Length..];
        Assert.IsTrue(
            Guid.TryParseExact(suffix, "B", out _),
            "The production lock name must end in a brace-form GUID, because the contract pins that identity.");
    }

    [TestMethod]
    public void TestLockNamesAreDistinctFromTheProductionName()
    {
        Assert.AreNotEqual(
            SingleInstanceLock.MutexName,
            _lockName,
            "A behavioural case must never contend with a real session for the production lock.");
        Assert.DoesNotContain("SoulComputerUse-{", _lockName);
    }

    [TestMethod]
    public void FirstAcquisitionTakesTheLock()
    {
        using SingleInstanceLock first = SingleInstanceLock.Acquire(_lockName);

        Assert.IsTrue(first.IsHeld, "The first acquisition on an idle name must take the lock.");
        Assert.AreEqual(LockAcquireOutcome.Acquired, first.Outcome);
    }

    [TestMethod]
    public void SecondAcquisitionIsRefusedWithoutWaiting()
    {
        using SingleInstanceLock first = SingleInstanceLock.Acquire(_lockName);
        Assert.IsTrue(first.IsHeld, "The first acquisition must succeed for this test to mean anything.");

        using SingleInstanceLock second = SingleInstanceLock.Acquire(_lockName);

        Assert.AreEqual(
            LockAcquireOutcome.HeldElsewhere,
            second.Outcome,
            "A second acquisition inside the same process must be refused.");
        Assert.IsFalse(second.IsHeld, "The nested acquisition must not claim ownership.");
        Assert.IsTrue(first.IsHeld, "The refused acquisition must leave the real owner holding the lock.");
    }

    [TestMethod]
    public void LockHeldByAnotherProcessIsRefused()
    {
        Process holder = StartHolder(HolderHoldMilliseconds);
        try
        {
            using SingleInstanceLock attempt = SingleInstanceLock.Acquire(_lockName);

            Assert.AreEqual(
                LockAcquireOutcome.HeldElsewhere,
                attempt.Outcome,
                "A lock held by a live second process must be reported, never queued for.");
            Assert.IsFalse(attempt.IsHeld, "This process must not believe it owns a lock another process holds.");
        }
        finally
        {
            KillHolder(holder);
        }
    }

    /// <summary>
    /// Documents the known cost of the handle model: any open handle keeps the object alive, so a
    /// process that only opens a handle does block acquisition. The contract treats this as a
    /// diagnostic situation rather than a blocking defect, because the lock name belongs to this
    /// product alone and handles close when their process exits.
    /// </summary>
    [TestMethod]
    public void AForeignHandleAloneBlocksAcquisitionUntilItCloses()
    {
        using (Mutex foreignHandle = new(initiallyOwned: false, _lockName))
        {
            Assert.IsTrue(
                SingleInstanceLock.IsHeldByAnyone(_lockName),
                "The foreign handle must have created the object, otherwise this case proves nothing.");

            using SingleInstanceLock blocked = SingleInstanceLock.Acquire(_lockName);

            Assert.AreEqual(
                LockAcquireOutcome.HeldElsewhere,
                blocked.Outcome,
                "With the handle model, an open handle is what 'occupied' means, even when nobody owns it.");
        }

        using SingleInstanceLock afterHandleClosed = SingleInstanceLock.Acquire(_lockName);

        Assert.IsTrue(
            afterHandleClosed.IsHeld,
            "Once the only handle closes, the object goes away and the lock must be obtainable again without a restart.");
    }

    [TestMethod]
    public void ReleasingTheLockLetsTheNextOwnerIn()
    {
        using (SingleInstanceLock first = SingleInstanceLock.Acquire(_lockName))
        {
            Assert.IsTrue(first.IsHeld, "The first acquisition must succeed.");
        }

        using SingleInstanceLock second = SingleInstanceLock.Acquire(_lockName);

        Assert.IsTrue(second.IsHeld, "After the owner releases the lock, the next acquisition must take it.");
    }

    [TestMethod]
    public void ReleasingFromAThreadThatDidNotAcquireWorks()
    {
        SingleInstanceLock? acquiredOnAnotherThread = null;
        Thread owner = new(() => acquiredOnAnotherThread = SingleInstanceLock.Acquire(_lockName));
        owner.Start();
        Assert.IsTrue(owner.Join(TimeSpan.FromSeconds(15)), "The acquiring thread did not finish.");

        SingleInstanceLock lockTakenElsewhere = acquiredOnAnotherThread
            ?? throw new InvalidOperationException("The acquiring thread did not report a lock.");
        Assert.IsTrue(lockTakenElsewhere.IsHeld, "The acquiring thread must have taken the lock.");

        lockTakenElsewhere.Dispose();

        using SingleInstanceLock reacquired = SingleInstanceLock.Acquire(_lockName);
        Assert.IsTrue(
            reacquired.IsHeld,
            "Closing the handle from another thread must free the lock, because the shutdown path does exactly that.");
    }

    [TestMethod]
    public void DisposeIsIdempotent()
    {
        SingleInstanceLock acquired = SingleInstanceLock.Acquire(_lockName);
        Assert.IsTrue(acquired.IsHeld, "The acquisition must succeed.");

        acquired.Dispose();
        acquired.Dispose();

        using SingleInstanceLock again = SingleInstanceLock.Acquire(_lockName);
        Assert.IsTrue(again.IsHeld, "After disposing twice the lock must still be free.");
    }

    [TestMethod]
    public void LockOfAKilledProcessIsAvailableAgain()
    {
        Process holder = StartHolder(HolderHoldMilliseconds);
        try
        {
            using SingleInstanceLock attempt = SingleInstanceLock.Acquire(_lockName);
            Assert.AreEqual(
                LockAcquireOutcome.HeldElsewhere,
                attempt.Outcome,
                "A live second process must be seen as the holder.");
        }
        finally
        {
            KillHolder(holder);
        }

        using SingleInstanceLock afterKill = SingleInstanceLock.Acquire(_lockName);

        Assert.IsTrue(
            afterKill.IsHeld,
            "Killing the holder closes its handle, so the next process must be able to take the lock without any abandonment event.");
        Assert.AreEqual(
            LockAcquireOutcome.Acquired,
            afterKill.Outcome,
            "Occupancy is handle based, so a dead holder leaves a plain acquisition behind rather than an abandonment.");
    }

    [TestMethod]
    public void ProbeDoesNotCreateTheLockObject()
    {
        Assert.IsFalse(
            SingleInstanceLock.IsHeldByAnyone(_lockName),
            "Before any holder exists, the probe must report no object.");

        using (SingleInstanceLock owner = SingleInstanceLock.Acquire(_lockName))
        {
            Assert.IsTrue(owner.IsHeld, "The acquisition after a probe must still take the lock.");
            Assert.IsTrue(SingleInstanceLock.IsHeldByAnyone(_lockName), "With a live holder the probe must report the object.");
        }
    }

    private Process StartHolder(int holdMilliseconds)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = TestSupportPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--hold-lock");
        startInfo.ArgumentList.Add(_lockName);
        startInfo.ArgumentList.Add(holdMilliseconds.ToString(CultureInfo.InvariantCulture));

        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The lock holder process did not start.");
        string? readiness = process.StandardOutput.ReadLine();
        if (readiness != "held")
        {
            string error = process.StandardError.ReadToEnd();
            KillHolder(process);
            throw new InvalidOperationException(
                $"The lock holder did not report readiness. First line: '{readiness}'. Stderr: '{error}'.");
        }

        return process;
    }

    private static void KillHolder(Process holder)
    {
        if (!holder.HasExited)
        {
            holder.Kill(entireProcessTree: true);
            if (!holder.WaitForExit(TimeSpan.FromSeconds(10)) || !holder.HasExited)
            {
                // Disposing a still-running child only releases the handle and leaves an orphan that
                // the next run would see as a dirty machine, so this is a failure, not a warning.
                throw new InvalidOperationException(
                    $"The lock holder process (id {holder.Id}) was still running ten seconds after being killed.");
            }
        }

        holder.Dispose();
    }
}