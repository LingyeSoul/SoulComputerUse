using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Windows.Integrity;

namespace SoulComputerUse.SessionAgent;

/// <summary>
/// The agent that runs inside the virtual desktop session. It refuses to run on the operator's
/// console session, binds itself to the owner's child session, detects its own credential gate tier,
/// starts the desktop shell, and serves the secured agent pipe until the host disconnects or the
/// unclaimed gate expires.
/// </summary>
/// <remarks>
/// The process is built as a window-subsystem application, so the child session's entry starts it
/// without a console window on the desktop the operator is watching. It attaches to the caller's
/// console only when the caller has one, which keeps standard error readable for manual runs from a
/// terminal.
/// </remarks>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // Windowless by design; this only restores standard error when a terminal started the process.
        AgentConsole.AttachToParentConsoleWhenPresent();

        // A directly started agent never passes the launcher's integrity check, so it verifies its own file
        // before it parses an argument or touches the machine.
        if (!SelfIntegrity.IsAccepted(SelfIntegrity.Check()))
        {
            AgentLog.Write("critical", "gate.install_integrity");
            Console.Error.WriteLine(SelfIntegrity.FailureMessage);
            return AgentExitCode.InstallIntegrity;
        }

        AgentOptions options;
        try
        {
            options = AgentCommandLine.Parse(args);
        }
        catch (ArgumentException error)
        {
            AgentLog.Write("error", "agent.arguments_invalid");
            Console.Error.WriteLine(error.Message);
            Console.Error.WriteLine(AgentCommandLine.Usage());
            return AgentExitCode.InvalidArguments;
        }

        try
        {
            return await AgentSessionRunner
                .RunAsync(options, AgentHelloFacts.Unavailable, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            AgentLog.Write("information", "agent.cancelled");
            return AgentExitCode.Success;
        }
        catch (SessionOperationException error)
        {
            AgentLog.Write("error", "agent.pipe_failed");
            Console.Error.WriteLine(error.Message);
            return AgentExitCode.StartFailure;
        }
        catch (AgentGateDeclarationException error)
        {
            // The declaration contradicted what this session detected. The runner already logged the
            // mismatch; the message names the contradiction, never the values.
            Console.Error.WriteLine(error.Message);
            return AgentExitCode.InvalidArguments;
        }
        catch (Exception error) when (IsExpectedStartFailure(error))
        {
            // Fail closed: an agent that cannot prove where it is must never keep running.
            AgentLog.Write("critical", "agent.start_failed");
            Console.Error.WriteLine(error.Message);
            return AgentExitCode.StartFailure;
        }
    }

    private static bool IsExpectedStartFailure(Exception error)
    {
        return error is InvalidOperationException
            || error is IOException
            || error is Win32Exception
            || error is UnauthorizedAccessException;
    }
}