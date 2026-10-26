using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
/// </remarks>
internal static class Program
{
    private static async Task<int> Main()
    {
        UTF8Encoding encoding = new(encoderShouldEmitUTF8Identifier: false);

        using StreamReader input = new(Console.OpenStandardInput(), encoding, detectEncodingFromByteOrderMarks: false);
        using StreamWriter output = new(Console.OpenStandardOutput(), encoding) { AutoFlush = true };
        await using NamedPipeSessionClient client = new();

        McpServer server = new(client, input, output, Console.Error);
        await server.RunAsync(CancellationToken.None);
        return 0;
    }
}