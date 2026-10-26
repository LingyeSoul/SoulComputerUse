using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Protocol;

namespace SoulComputerUse.SessionAgent;

/// <summary>
/// The agent that runs inside the virtual desktop session. It refuses to run on the operator's
/// console session, binds itself to the owner's child session, detects its own credential gate tier,
/// starts the desktop shell, and serves the secured agent pipe until the host disconnects or the
/// unclaimed gate expires.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
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