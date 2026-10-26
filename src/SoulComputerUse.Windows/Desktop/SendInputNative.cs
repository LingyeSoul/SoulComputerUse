using System.Runtime.InteropServices;

namespace SoulComputerUse.Windows.Desktop;

/// <summary>The one user32 entry point desktop input injection needs.</summary>
internal static class SendInputNative
{
    [DllImport("user32.dll", EntryPoint = "SendInput", SetLastError = true)]
    internal static extern uint SendInput(uint inputCount, [In] INPUT[] inputs, int inputSize);
}