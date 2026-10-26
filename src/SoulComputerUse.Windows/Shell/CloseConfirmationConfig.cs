using System;
using System.Collections.Generic;

namespace SoulComputerUse.Windows.Shell;

/// <summary>Which button the close confirmation offers.</summary>
public enum CloseConfirmationButton
{
    /// <summary>Keep the session running; this is the default and what Enter and Esc choose.</summary>
    Cancel = 0,

    /// <summary>End the session and log the virtual desktop off; always the right-hand button.</summary>
    EndSession = 1,
}

/// <summary>The icon the close confirmation shows.</summary>
public enum CloseConfirmationIcon
{
    /// <summary>The warning icon, because the action is not reversible.</summary>
    Warning = 0,
}

/// <summary>
/// The whole content and button layout of the close confirmation, as data. The dialog itself is a thin
/// adapter over this value, so "the default button is cancel", "the destructive button is on the
/// right" and "the body is the agreed sentence, word for word" can be asserted without opening a
/// window and without reading a screenshot.
/// </summary>
public sealed record CloseConfirmationConfig
{
    /// <summary>The dialog title pinned by the native title bar spec.</summary>
    public const string SpecTitle = "结束会话？";

    /// <summary>
    /// The dialog body pinned by the native title bar spec. It is one sentence on purpose: the whole
    /// point is that the operator reads it once and understands that the work will be lost.
    /// </summary>
    public const string SpecBody = "将立即结束当前会话并注销虚拟桌面，未保存的工作将丢失且不可撤销。";

    /// <summary>The identifier of the cancel button inside the dialog.</summary>
    public const int CancelButtonId = 1001;

    /// <summary>The identifier of the end-session button inside the dialog.</summary>
    public const int EndSessionButtonId = 1002;

    /// <summary>Builds a configuration and refuses one that could not be answered safely.</summary>
    public CloseConfirmationConfig(
        string title,
        string body,
        CloseConfirmationButton defaultButton,
        CloseConfirmationButton destructiveButton,
        CloseConfirmationIcon icon)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);
        if (defaultButton == destructiveButton)
        {
            throw new ArgumentException(
                $"The default button and the destructive button are both {defaultButton}, so a stray Enter would end the session.",
                nameof(destructiveButton));
        }

        Title = title;
        Body = body;
        DefaultButton = defaultButton;
        DestructiveButton = destructiveButton;
        Icon = icon;
        Buttons = [CloseConfirmationButton.Cancel, destructiveButton];
    }

    /// <summary>
    /// The configuration every close confirmation uses: warning icon, cancel as the default and the
    /// end-session button to its right.
    /// </summary>
    public static CloseConfirmationConfig Default { get; } = new(
        SpecTitle,
        SpecBody,
        CloseConfirmationButton.Cancel,
        CloseConfirmationButton.EndSession,
        CloseConfirmationIcon.Warning);

    /// <summary>The dialog title.</summary>
    public string Title { get; }

    /// <summary>The dialog body, word for word from the spec.</summary>
    public string Body { get; }

    /// <summary>The button Enter picks, and the one Escape means too.</summary>
    public CloseConfirmationButton DefaultButton { get; }

    /// <summary>The button that ends the session.</summary>
    public CloseConfirmationButton DestructiveButton { get; }

    /// <summary>The icon shown next to the body.</summary>
    public CloseConfirmationIcon Icon { get; }

    /// <summary>The buttons in display order, left to right; the destructive one is last.</summary>
    public IReadOnlyList<CloseConfirmationButton> Buttons { get; }

    /// <summary>The button Escape and the dialog close box mean.</summary>
    public CloseConfirmationButton EscapeButton => DefaultButton;

    /// <summary>Returns the label of a button as the dialog must show it.</summary>
    public static string LabelOf(CloseConfirmationButton button)
    {
        return button switch
        {
            CloseConfirmationButton.Cancel => "取消",
            CloseConfirmationButton.EndSession => "结束会话",
            _ => throw new ArgumentOutOfRangeException(nameof(button), button, "Unknown confirmation button."),
        };
    }

    /// <summary>Returns the identifier the dialog uses for a button.</summary>
    public static int IdOf(CloseConfirmationButton button)
    {
        return button switch
        {
            CloseConfirmationButton.Cancel => CancelButtonId,
            CloseConfirmationButton.EndSession => EndSessionButtonId,
            _ => throw new ArgumentOutOfRangeException(nameof(button), button, "Unknown confirmation button."),
        };
    }
}
