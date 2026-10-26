using System;
using System.Runtime.InteropServices;

namespace SoulComputerUse.Host;

/// <summary>
/// Decides whether a startup failure can be reported on the error stream at all. A GUI process started
/// by a double click has no console and no redirected handle, so writing nowhere would hide the reason;
/// such a run shows a dialog instead. A run whose error stream is a pipe, a file or a console reports
/// there and exits, so an automated acceptance run never blocks on a dialog nobody can dismiss.
/// </summary>
internal static class HostConsole
{
    private const int StandardErrorHandle = -12;
    private const uint FileTypeUnknown = 0;
    private const uint FileTypeCharacter = 2;
    private const uint FileTypePipe = 3;
    private const uint FileTypeDisk = 1;

    /// <summary>Reports whether the error stream can carry the message to a reader.</summary>
    internal static bool HasUsableErrorStream()
    {
        nint handle = GetStdHandle(StandardErrorHandle);
        if (handle == 0 || handle == -1)
        {
            return false;
        }

        uint fileType = GetFileType(handle);
        return fileType is FileTypeCharacter or FileTypePipe or FileTypeDisk;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(nint handle);
}