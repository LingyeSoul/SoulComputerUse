using System;
using System.Runtime.InteropServices;
using SoulComputerUse.Windows.Native;

namespace SoulComputerUse.Windows.Shell;

/// <summary>
/// The thin adapter that turns <see cref="CloseConfirmationConfig"/> into the real system modal
/// confirmation. Everything decidable lives in the configuration; this type only builds the native
/// description, shows it and reports the answer.
/// </summary>
/// <remarks>
/// Showing the dialog is modal and needs an operator, so no automated test calls this. What the tests
/// assert is the configuration it is handed: the exact sentence, the default button, the button order
/// and the icon.
/// </remarks>
public static class CloseConfirmationDialog
{
    /// <summary>
    /// Shows the confirmation owned by a window and returns true only when the operator chose to end
    /// the session.
    /// </summary>
    /// <param name="owner">The window the dialog is modal to.</param>
    /// <param name="config">The content and button layout to show.</param>
    public static bool Confirm(nint owner, CloseConfirmationConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        int buttonSize = Marshal.SizeOf<TaskDialogButton>();
        nint buttonArray = Marshal.AllocHGlobal(buttonSize * config.Buttons.Count);
        try
        {
            for (int index = 0; index < config.Buttons.Count; index++)
            {
                CloseConfirmationButton button = config.Buttons[index];
                TaskDialogButton nativeButton = new()
                {
                    Id = CloseConfirmationConfig.IdOf(button),
                    Text = CloseConfirmationConfig.LabelOf(button),
                };
                Marshal.StructureToPtr(nativeButton, buttonArray + (index * buttonSize), false);
            }

            TaskDialogConfig nativeConfig = new()
            {
                Size = Marshal.SizeOf<TaskDialogConfig>(),
                ParentWindow = owner,
                Flags = TaskDialogNative.TdfPositionRelativeToWindow
                    | TaskDialogNative.TdfAllowDialogCancellation,
                WindowTitle = config.Title,

                // The standard warning icon is passed as its resource identifier: the flag that switches
                // the field to an icon handle is not set, which is what the system expects for TD_*_ICON.
                MainIcon = TaskDialogNative.WarningIcon,
                Content = config.Body,
                ButtonCount = config.Buttons.Count,
                Buttons = buttonArray,
                DefaultButton = CloseConfirmationConfig.IdOf(config.DefaultButton),
            };

            int result = Show(ref nativeConfig, out int pressedButton);
            if (result < 0)
            {
                throw new InvalidOperationException(
                    $"TaskDialogIndirect failed with HRESULT 0x{result:X8} while showing the close confirmation.");
            }

            return pressedButton == CloseConfirmationConfig.IdOf(config.DestructiveButton);
        }
        finally
        {
            Marshal.FreeHGlobal(buttonArray);
        }
    }

    private static int Show(ref TaskDialogConfig nativeConfig, out int pressedButton)
    {
        try
        {
            return TaskDialogNative.TaskDialogIndirect(ref nativeConfig, out pressedButton, out _, out _);
        }
        catch (EntryPointNotFoundException error)
        {
            // One clear reason instead of a bare binding failure: the task dialog only exists in
            // Common Controls 6, which a process has to request in its manifest.
            throw new InvalidOperationException(
                "The task dialog API is unavailable, so the close confirmation cannot be shown. The application manifest must request Microsoft.Windows.Common-Controls version 6.",
                error);
        }
    }
}
