using System;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Windows.Ipc;

namespace SoulComputerUse.Cli;

/// <summary>
/// Entry point of the CLI adapter. It connects to the session owner over the contract's named pipe.
/// No arguments opens the prompt, and one command runs once and exits.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        await using NamedPipeSessionClient client = new();
        CliRunner runner = new(client, Console.In, Console.Out, Console.Error);

        try
        {
            if (args.Length == 0)
            {
                return await runner.RunReplAsync(CancellationToken.None);
            }

            return await runner.RunSingleAsync(args, CancellationToken.None);
        }
        catch (Exception error)
        {
            // This is the process boundary, not a recovery path: it keeps a stack trace, whose
            // absolute paths the log whitelist forbids, away from the error stream.
            Console.Error.WriteLine($"E_IPC_PROTOCOL The CLI stopped unexpectedly ({error.GetType().Name}).");
            return 1;
        }
    }
}