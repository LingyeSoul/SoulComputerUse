using System;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Hosting.Discovery;
using SoulComputerUse.Windows.Ipc;

namespace SoulComputerUse.Cli;

/// <summary>
/// Entry point of the CLI adapter. It attaches to the session owner over the contract's named pipe,
/// starting a session only when section 3 of the contract allows it. No arguments opens the prompt,
/// and one command runs once and exits.
/// </summary>
/// <remarks>
/// A command line is validated before the machine is probed, so a typo is answered as an input error
/// rather than as "no owner". A failed discovery is reported as one contract error line on the error
/// stream with a non-zero exit code; the process prints no stack trace, whose absolute paths the log
/// whitelist forbids.
/// </remarks>
internal static class Program
{
    private const int FailureExitCode = 1;
    private const string OkLine = "ok";

    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0)
        {
            CliCommand command;
            try
            {
                command = CliCommandParser.Parse(args);
            }
            catch (CliParseException error)
            {
                // Parsed once before probing so a typo is answered as an input error rather than as a
                // missing owner. CliRunner parses the same arguments again for its own dispatch.
                WriteError(error.Code, error.Message);
                return FailureExitCode;
            }

            // Help and quit are the two commands section 6 marks as local. They must work whether or
            // not a session exists, so neither probes the machine; CliRunner answers both the same way.
            if (command.Kind == CliCommandKind.Help)
            {
                Console.Out.WriteLine(CliHelp.Text);
                return 0;
            }

            if (command.Kind == CliCommandKind.Quit)
            {
                Console.Out.WriteLine(OkLine);
                return 0;
            }
        }

        try
        {
            // The starter stays null: this version cannot start a session by itself, so a free lock
            // without an owner fails with E_NOT_READY instead of pretending a session exists. A host
            // layer that implements IOwnerSessionStarter injects it here, and discovery then starts
            // the session through that starter.
            OwnerSessionResolver resolver = new(
                new OwnerProbe(),
                static () => new NamedPipeSessionClient(),
                null);

            await using ResolvedSession session = await resolver.ResolveAsync(CancellationToken.None);
            CliRunner runner = new(session.Client, Console.In, Console.Out, Console.Error);

            if (args.Length == 0)
            {
                return await runner.RunReplAsync(CancellationToken.None);
            }

            return await runner.RunSingleAsync(args, CancellationToken.None);
        }
        catch (SessionOperationException error)
        {
            WriteError(error.Code, error.Message);
            return FailureExitCode;
        }
        catch (Exception error)
        {
            // This is the process boundary, not a recovery path: it keeps a stack trace, whose
            // absolute paths the log whitelist forbids, away from the error stream.
            Console.Error.WriteLine($"E_IPC_PROTOCOL The CLI stopped unexpectedly ({error.GetType().Name}).");
            return FailureExitCode;
        }
    }

    private static void WriteError(SessionErrorCode code, string message)
    {
        Console.Error.WriteLine($"{SessionErrorCodeNames.ToWireName(code)} {AsSingleLine(message)}");
    }

    private static string AsSingleLine(string message)
    {
        return message.Replace('\r', ' ').Replace('\n', ' ');
    }
}
