using System;
using System.Runtime.InteropServices;

namespace SoulComputerUse.Windows.Native;

/// <summary>
/// The task-dialog surface of comctl32, kept apart from <see cref="NativeMethods"/> because it belongs
/// to the Common Controls library and only works when the process asks for Common Controls 6.
/// </summary>
internal static class TaskDialogNative
{
    internal const int TdfAllowDialogCancellation = 0x0008;
    internal const int TdfPositionRelativeToWindow = 0x1000;
    internal const int TdfUseMainIconHandle = 0x2000;

    internal const int IdiWarning = 32515;

    [DllImport("comctl32.dll", EntryPoint = "TaskDialogIndirect", CharSet = CharSet.Unicode)]
    internal static extern int TaskDialogIndirect(
        ref TaskDialogConfig config,
        out int buttonId,
        out int radioButtonId,
        [MarshalAs(UnmanagedType.Bool)] out bool verificationChecked);

    [DllImport("user32.dll", EntryPoint = "LoadIconW", SetLastError = true)]
    internal static extern nint LoadIconW(nint instance, nint iconName);
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct TaskDialogConfig
{
    internal int Size;
    internal nint ParentWindow;
    internal nint Instance;
    internal int Flags;
    internal int CommonButtons;
    [MarshalAs(UnmanagedType.LPWStr)]
    internal string? WindowTitle;
    internal nint MainIcon;
    [MarshalAs(UnmanagedType.LPWStr)]
    internal string? MainInstruction;
    [MarshalAs(UnmanagedType.LPWStr)]
    internal string? Content;
    internal int ButtonCount;
    internal nint Buttons;
    internal int DefaultButton;
    internal int RadioButtonCount;
    internal nint RadioButtons;
    internal int DefaultRadioButton;
    [MarshalAs(UnmanagedType.LPWStr)]
    internal string? VerificationText;
    [MarshalAs(UnmanagedType.LPWStr)]
    internal string? ExpandedInformation;
    [MarshalAs(UnmanagedType.LPWStr)]
    internal string? ExpandedControlText;
    [MarshalAs(UnmanagedType.LPWStr)]
    internal string? CollapsedControlText;
    internal nint FooterIcon;
    [MarshalAs(UnmanagedType.LPWStr)]
    internal string? Footer;
    internal nint Callback;
    internal nint CallbackData;
    internal int Width;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct TaskDialogButton
{
    internal int Id;
    [MarshalAs(UnmanagedType.LPWStr)]
    internal string Text;
}
