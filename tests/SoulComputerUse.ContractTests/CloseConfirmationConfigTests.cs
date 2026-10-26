using System;
using SoulComputerUse.Windows.Shell;

namespace SoulComputerUse.ContractTests;

/// <summary>
/// Contract tests for the close confirmation content. The dialog itself is a thin adapter, so every
/// measurable promise about it — the exact sentence, the default button, the button order, the icon —
/// is asserted here as data, which is why no screenshot is needed for these.
/// </summary>
[TestClass]
public sealed class CloseConfirmationConfigTests
{
    [TestMethod]
    public void TheTitleAndBodyAreTheSpecifiedSentencesWordForWord()
    {
        CloseConfirmationConfig config = CloseConfirmationConfig.Default;

        Assert.AreEqual("结束会话？", config.Title, "The title must be the specified one.");
        Assert.AreEqual(
            "将立即结束当前会话并注销虚拟桌面，未保存的工作将丢失且不可撤销。",
            config.Body,
            "The body must match the spec character for character; a reworded warning is a defect, not a nicety.");
    }

    [TestMethod]
    public void TheDefaultButtonIsCancelSoAStrayEnterChangesNothing()
    {
        CloseConfirmationConfig config = CloseConfirmationConfig.Default;

        Assert.AreEqual(
            CloseConfirmationButton.Cancel,
            config.DefaultButton,
            "Cancel is the default, so Enter and Esc both keep the session alive.");
    }

    [TestMethod]
    public void TheDestructiveButtonIsEndSessionAndSitsOnTheRight()
    {
        CloseConfirmationConfig config = CloseConfirmationConfig.Default;

        Assert.AreEqual(CloseConfirmationButton.EndSession, config.DestructiveButton);
        Assert.AreEqual("结束会话", CloseConfirmationConfig.LabelOf(config.DestructiveButton), "The destructive label is the specified one.");
        Assert.AreEqual("取消", CloseConfirmationConfig.LabelOf(config.DefaultButton), "The cancel label is the specified one.");
        Assert.AreNotEqual(
            config.DefaultButton,
            config.DestructiveButton,
            "The default and destructive buttons must be different, otherwise one key press could end the session.");
    }

    [TestMethod]
    public void TheButtonsAreOrderedWithTheDestructiveOneLastAndEscapeMeansCancel()
    {
        CloseConfirmationConfig config = CloseConfirmationConfig.Default;

        Assert.HasCount(2, config.Buttons, "The dialog offers exactly two buttons.");
        Assert.AreEqual(CloseConfirmationButton.Cancel, config.Buttons[0], "Cancel is the left-hand button.");
        Assert.AreEqual(
            CloseConfirmationButton.EndSession,
            config.Buttons[^1],
            "The destructive button is the right-hand one, which is the Windows convention for it.");
        Assert.AreEqual(
            CloseConfirmationButton.Cancel,
            config.EscapeButton,
            "Escape and the dialog close box mean cancel, never end session.");
        Assert.AreEqual(1001, CloseConfirmationConfig.IdOf(CloseConfirmationButton.Cancel));
        Assert.AreEqual(1002, CloseConfirmationConfig.IdOf(CloseConfirmationButton.EndSession));
    }

    [TestMethod]
    public void TheIconIsTheWarningIcon()
    {
        Assert.AreEqual(CloseConfirmationIcon.Warning, CloseConfirmationConfig.Default.Icon);
    }

    [TestMethod]
    public void AConfigurationWhereTheDefaultWouldDestroyIsRefused()
    {
        ArgumentException error = Assert.ThrowsExactly<ArgumentException>(
            () => new CloseConfirmationConfig(
                CloseConfirmationConfig.SpecTitle,
                CloseConfirmationConfig.SpecBody,
                CloseConfirmationButton.EndSession,
                CloseConfirmationButton.EndSession,
                CloseConfirmationIcon.Warning));

        Assert.Contains("Enter", error.Message);
    }

    [TestMethod]
    public void AConfigurationWithoutTextIsRefused()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => new CloseConfirmationConfig(
                " ",
                CloseConfirmationConfig.SpecBody,
                CloseConfirmationButton.Cancel,
                CloseConfirmationButton.EndSession,
                CloseConfirmationIcon.Warning));
        Assert.ThrowsExactly<ArgumentException>(
            () => new CloseConfirmationConfig(
                CloseConfirmationConfig.SpecTitle,
                "  ",
                CloseConfirmationButton.Cancel,
                CloseConfirmationButton.EndSession,
                CloseConfirmationIcon.Warning));
    }
}
