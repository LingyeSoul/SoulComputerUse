namespace SoulComputerUse.Cli;

/// <summary>The command table that <c>scu --help</c> prints, mirroring section 6 of the contract.</summary>
internal static class CliHelp
{
    /// <summary>The complete help text, one command per line.</summary>
    internal const string Text = """
        Usage: scu [command [arguments]]
        Commands:
          start
          status
          shot [--out <path>]
          move <x> <y>
          click <x> <y> --button <left|right|middle> --count <n>
          scroll <x> <y> <dx> <dy>
          type <text>
          key <k1>+<k2>...
          quit
        Options:
          --help
        Run without arguments to open the interactive prompt (scu>). A command given on the
        command line runs once and exits. quit exits the prompt without closing the session.
        start is local: it opens the product window, and the operator logs on in that window.
        """;
}