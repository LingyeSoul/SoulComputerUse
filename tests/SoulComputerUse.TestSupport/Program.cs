using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading;
using SoulComputerUse.Windows.SingleInstance;

namespace SoulComputerUse.TestSupport;

/// <summary>
/// Helper process for tests that need a second real process holding a lock. It takes the named lock
/// and keeps it until it exits, so tests can prove both "another process holds it" and "the holder
/// died holding it, and the next owner is unaffected".
/// </summary>
/// <remarks>
/// The lock name is always supplied by the test and is never the production name: a leftover helper
/// must be unable to disturb a real session on the machine.
/// </remarks>
internal static class Program
{
    private const int LockUnavailableExitCode = 2;
    private const int BadArgumentsExitCode = 64;

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The helper must keep the lock handle open until the process ends, which is the whole point of this test helper.")]
    private static int Main(string[] args)
    {
        if (args.Length != 3 || args[0] != "--hold-lock")
        {
            Console.Error.WriteLine("usage: SoulComputerUse.TestSupport --hold-lock <lock-name> <milliseconds>");
            return BadArgumentsExitCode;
        }

        if (!int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int holdMilliseconds)
            || holdMilliseconds < 0)
        {
            Console.Error.WriteLine("--hold-lock needs a non-negative whole number of milliseconds.");
            return BadArgumentsExitCode;
        }

        SingleInstanceLock held = SingleInstanceLock.Acquire(args[1]);
        if (!held.IsHeld)
        {
            Console.Error.WriteLine("the named lock was already held");
            return LockUnavailableExitCode;
        }

        Console.Out.WriteLine("held");
        Console.Out.Flush();
        Thread.Sleep(holdMilliseconds);

        // Exiting without disposing leaves the handle to be closed by the kernel at process exit.
        Environment.Exit(0);
        return 0;
    }
}