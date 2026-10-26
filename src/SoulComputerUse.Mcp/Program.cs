using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Hosting.Discovery;
using SoulComputerUse.Windows.Ipc;

namespace SoulComputerUse.Mcp;

/// <summary>
/// Entry point of the MCP stdio adapter: it serves the session tool surface on standard input and
/// standard output, newline-delimited JSON-RPC 2.0, UTF-8, with diagnostics on standard error only.
/// </summary>
/// <remarks>
/// The adapter talks to the session owner over the contract's owner named pipe. It never opens a
/// network listener, so the only way in is the hosting process's standard streams and the only way
/// out is the pipe.
/// <para>
/// Discovery runs before the standard output writer exists. When it fails, nothing is written to
/// standard output: the MCP client has not sent a request, so no JSON-RPC message would be valid.
/// The failure goes to standard error with a non-zero exit code instead.
/// </para>
/// </remarks>
internal static class Program
{
    private const int FailureExitCode = 1;

    private static async Task<int> Main()
    {
        // The starter stays null: this version cannot start a session by itself, so a free lock
        // without an owner fails with E_NOT_READY. A host layer that implements IOwnerSessionStarter
        // injects it here, and discovery then starts the session through that starter.
        OwnerSessionResolver resolver = new(
            new OwnerProbe(),
            static () => new NamedPipeSessionClient(),
            null);

        ResolvedSession session;
        try
        {
            session = await resolver.ResolveAsync(CancellationToken.None);
        }
        catch (SessionOperationException error)
        {
            Console.Error.WriteLine($"{SessionErrorCodeNames.ToWireName(error.Code)} {AsSingleLine(error.Message)}");
            return FailureExitCode;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"E_IPC_PROTOCOL The MCP adapter stopped unexpectedly ({error.GetType().Name}).");
            return FailureExitCode;
        }

        await using (session)
        {
            UTF8Encoding encoding = new(encoderShouldEmitUTF8Identifier: false);

            using StreamReader input = new(Console.OpenStandardInput(), encoding, detectEncodingFromByteOrderMarks: false);
            using StreamWriter output = new(Console.OpenStandardOutput(), encoding) { AutoFlush = true };

            McpServer server = new(session.Client, input, output, Console.Error);
            await server.RunAsync(CancellationToken.None);
            return 0;
        }
    }

    private static string AsSingleLine(string message)
    {
        return message.Replace('\r', ' ').Replace('\n', ' ');
    }
}
