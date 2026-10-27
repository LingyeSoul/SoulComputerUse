using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Hosting.Discovery;
using SoulComputerUse.Windows.Hosting.Headless;
using SoulComputerUse.Windows.Hosting.Launch;
using SoulComputerUse.Windows.Hosting.Startup.Split;
using SoulComputerUse.Windows.Ipc;

namespace SoulComputerUse.Mcp;

/// <summary>
/// Entry point of the MCP stdio adapter: it serves the session tool surface on standard input and
/// standard output, newline-delimited JSON-RPC 2.0, UTF-8, with diagnostics on standard error only.
/// </summary>
/// <remarks>
/// <para>
/// The adapter talks to the session owner over the contract's owner named pipe, resolving one
/// session per tool call instead of binding one at startup. It therefore lives exactly as long as
/// the protocol session: a machine with no owner still gets answers, session tools report the
/// discovery failure as a tool error, and <c>start_session</c> can open the product window. It never
/// opens a network listener.
/// </para>
/// <para>
/// No session is resolved while the adapter starts. When a construction step fails, nothing is
/// written to standard output: the MCP client has not sent a request, so no JSON-RPC message would
/// be valid. The failure goes to standard error with a non-zero exit code instead.
/// </para>
/// <para>
/// Becoming the owner is opt-in: only <c>SOULCOMPUTERUSE_ALLOW_HEADLESS=1</c> injects the headless
/// starter, and with the variable absent a tool call on a free lock still reports
/// <c>E_NOT_READY</c>. See <see cref="HeadlessStarterActivation"/>.
/// </para>
/// </remarks>
internal static class Program
{
    private const int FailureExitCode = 1;

    private static async Task<int> Main()
    {
        try
        {
            // Section 3 allows a CLI or MCP process to become the session owner when the lock is free and
            // no owner answers. That path is behind HeadlessStarterActivation's environment gate: without
            // SOULCOMPUTERUSE_ALLOW_HEADLESS=1 discovery gets no starter and a tool call still fails a free
            // lock with E_NOT_READY, and with it the starter refuses this build's placeholder backend with
            // the same code before the lock is taken, so no path reports a session that cannot serve calls.
            IOwnerSessionStarter? starter = HeadlessStarterActivation.IsAllowed()
                ? new HeadlessOwnerStarter(
                    HeadlessStarterActivation.ProductionOptions(static () => new UnavailableSessionClient()),
                    new OwnerProbe())
                : null;

            OwnerSessionResolver resolver = new(
                new OwnerProbe(),
                static () => new NamedPipeSessionClient(),
                starter);

            ISessionAccessor sessions = new OwnerSessionAccessor(resolver);

            IHostStarter hostStarter = new HostProcessLauncher(
                new OwnerProbe(),
                new ConsoleSessionGuard(),
                new ElevationGuard(),
                new HostProcessStarter(),
                new BlockingWaitClock(),
                AppContext.BaseDirectory);

            UTF8Encoding encoding = new(encoderShouldEmitUTF8Identifier: false);

            using StreamReader input = new(Console.OpenStandardInput(), encoding, detectEncodingFromByteOrderMarks: false);
            using StreamWriter output = new(Console.OpenStandardOutput(), encoding) { AutoFlush = true };

            McpServer server = new(sessions, hostStarter, input, output, Console.Error);
            await server.RunAsync(CancellationToken.None);
            return 0;
        }
        catch (SessionOperationException error)
        {
            Console.Error.WriteLine($"{SessionErrorCodeNames.ToWireName(error.Code)} {AsSingleLine(error.Message)}");
            return FailureExitCode;
        }
        catch (Exception error)
        {
            // This is the process boundary, not a recovery path: it keeps a stack trace, whose
            // absolute paths the log whitelist forbids, away from the error stream.
            Console.Error.WriteLine($"E_IPC_PROTOCOL The MCP adapter stopped unexpectedly ({error.GetType().Name}).");
            return FailureExitCode;
        }
    }

    private static string AsSingleLine(string message)
    {
        return message.Replace('\r', ' ').Replace('\n', ' ');
    }
}
