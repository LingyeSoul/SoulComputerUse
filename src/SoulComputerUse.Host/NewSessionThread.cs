using System;
using System.Threading;

namespace SoulComputerUse.Host;

/// <summary>
/// Runs the new-session mode on a dedicated single-threaded-apartment thread.
/// </summary>
/// <remarks>
/// <para>
/// The thread is required twice over. The remote desktop control is an ActiveX control, which can only be
/// created and driven on an STA thread. The startup pipeline then waits synchronously on the logon gate,
/// so the thread must also carry no synchronization context: a context whose loop is blocked by a
/// synchronous wait would deadlock the very notifications that wait needs.
/// </para>
/// <para>
/// The mode pumps its own window messages while it waits, so the remote desktop control keeps painting the
/// Windows logon screen the operator is being asked to use.
/// </para>
/// </remarks>
internal static class NewSessionThread
{
    /// <summary>Runs one session and returns its exit code.</summary>
    /// <exception cref="ArgumentNullException">The argument array is null.</exception>
    internal static int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        int exitCode = 1;
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                using OleApartment apartment = OleApartment.Initialize();
                using NewSessionHost host = new();
                exitCode = host.Run();
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                failure = error;
            }
        })
        {
            IsBackground = false,
            Name = "SoulComputerUse new session",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            App.ShowStartupError("E_MANUAL_INTERVENTION " + failure.Message);
            return 1;
        }

        return exitCode;
    }
}