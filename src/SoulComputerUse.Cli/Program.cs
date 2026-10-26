using System;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Hosting.Discovery;
using SoulComputerUse.Windows.Hosting.Headless;
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
/// <para>
/// Becoming the owner is opt-in: only <c>SOULCOMPUTERUSE_ALLOW_HEADLESS=1</c> injects the headless
/// starter, and with the variable absent this process still refuses a free lock with
/// <c>E_NOT_READY</c>. See <see cref="HeadlessStarterActivation"/>.
/// </para>
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
            // Section 3 allows a CLI or MCP process to become the session owner when the lock is free
            // and no owner answers. That path is behind HeadlessStarterActivation's environment gate:
            // without SOULCOMPUTERUSE_ALLOW_HEADLESS=1 discovery gets no starter and still fails a free
            // lock with E_NOT_READY, and with it the starter refuses this build's placeholder backend
            // with the same code before the lock is taken, so no path reports a session that cannot
            // serve calls.
            IOwnerSessionStarter? starter = HeadlessStarterActivation.IsAllowed()
                ? new HeadlessOwnerStarter(
                    HeadlessStarterActivation.ProductionOptions(static () => new UnavailableSessionClient()),
                    new OwnerProbe())
                : null;

            OwnerSessionResolver resolver = new(
                new OwnerProbe(),
                static () => new NamedPipeSessionClient(),
                starter);

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
