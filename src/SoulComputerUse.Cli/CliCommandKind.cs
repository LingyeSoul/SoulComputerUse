namespace SoulComputerUse.Cli;

/// <summary>
/// The commands that section 6 of the interface contract defines for the CLI. The set is closed:
/// an unknown command is refused instead of being guessed.
/// </summary>
internal enum CliCommandKind
{
    /// <summary>Reports the session status, one structured line.</summary>
    Status = 0,

    /// <summary>Captures one PNG frame and writes it out or prints it as base64.</summary>
    Shot = 1,

    /// <summary>Moves the pointer.</summary>
    Move = 2,

    /// <summary>Clicks a mouse button a given number of times.</summary>
    Click = 3,

    /// <summary>Scrolls the wheel.</summary>
    Scroll = 4,

    /// <summary>Types text verbatim.</summary>
    Type = 5,

    /// <summary>Presses a key combination.</summary>
    Key = 6,

    /// <summary>Leaves the interactive prompt without closing the session.</summary>
    Quit = 7,

    /// <summary>Prints the command table.</summary>
    Help = 8,

    /// <summary>Opens the product window so the operator can log on there.</summary>
    Start = 9,
}