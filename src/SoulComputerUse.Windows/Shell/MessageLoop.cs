using System;
using SoulComputerUse.Windows.Native;

namespace SoulComputerUse.Windows.Shell;

/// <summary>
/// The message loop of the thread that owns the product window. It exists as its own type so the host
/// process does not have to declare interop of its own.
/// </summary>
public static class MessageLoop
{
    /// <summary>Runs the loop until <c>WM_QUIT</c> arrives and returns the quit code.</summary>
    public static int Run()
    {
        while (true)
        {
            int result = NativeMethods.GetMessageW(out Msg message, 0, 0, 0);
            if (result == 0)
            {
                return (int)message.WParam;
            }

            if (result < 0)
            {
                throw new InvalidOperationException("GetMessageW failed while pumping the window messages.");
            }

            _ = NativeMethods.TranslateMessage(ref message);
            _ = NativeMethods.DispatchMessageW(ref message);
        }
    }
}
