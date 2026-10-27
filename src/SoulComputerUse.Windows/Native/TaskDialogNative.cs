using System;
using System.Runtime.InteropServices;

namespace SoulComputerUse.Windows.Native;

/// <summary>
/// The task-dialog surface of comctl32, kept apart from <see cref="NativeMethods"/> because it belongs
/// to the Common Controls library and only works when the process asks for Common Controls 6.
/// </summary>
/// <remarks>
/// <c>commctrl.h</c> declares the task-dialog structures inside <c>pshpack1.h</c>, so they are packed to
/// one byte. The managed declarations carry the same packing: with the default packing the structure is
/// larger than the one the system expects, and <c>TaskDialogIndirect</c> answers <c>E_INVALIDARG</c>
/// before it ever shows a dialog.
/// </remarks>
internal static class TaskDialogNative
{
    internal const int TdfAllowDialogCancellation = 0x0008;
    internal const int TdfPositionRelativeToWindow = 0x1000;

    /// <summary>
    /// <c>TD_WARNING_ICON</c>, the standard warning icon as a resource identifier. The icon is passed
    /// as this value rather than as a handle, which is the form <c>pszMainIcon</c> accepts when
    /// <c>TDF_USE_HICON_MAIN</c> is not set.
    /// </summary>
    internal const nint WarningIcon = -1;

    [DllImport("comctl32.dll", EntryPoint = "TaskDialogIndirect", CharSet = CharSet.Unicode)]
    internal static extern int TaskDialogIndirect(
        ref TaskDialogConfig config,
        out int buttonId,
        out int radioButtonId,
        [MarshalAs(UnmanagedType.Bool)] out bool verificationChecked);
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
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

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
internal struct TaskDialogButton
{
    internal int Id;
    [MarshalAs(UnmanagedType.LPWStr)]
    internal string Text;
}
