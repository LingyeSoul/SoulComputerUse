using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using SoulComputerUse.Windows.Native;

namespace SoulComputerUse.SessionAgent;

/// <summary>
/// The agent that runs inside the virtual desktop session. Milestone M0 proves two things only:
/// an agent that lands in the console session exits at once, and an agent that really is in a
/// child session can bring the desktop shell up. Screenshot, input and the named-pipe server
/// arrive with milestone M2.
/// </summary>
internal static class Program
{
    private const int ConsoleSessionExitCode = 3;
    private const int StartFailureExitCode = 4;

    private static int Main()
    {
        try
        {
            return RunAgent();
        }
        catch (Exception error) when (IsExpectedStartFailure(error))
        {
            // Fail closed: an agent that cannot prove where it is must never keep running.
            WriteLogEvent("critical", "agent.start_failed");
            Console.Error.WriteLine(error.Message);
            return StartFailureExitCode;
        }
    }

    private static int RunAgent()
    {
        int processSessionId = WtsSessionProbe.GetCurrentProcessSessionId();
        int consoleSessionId = WtsSessionProbe.GetActiveConsoleSessionId();
        if (AgentSessionGate.Evaluate(processSessionId, consoleSessionId) == AgentSessionVerdict.ConsoleSession)
        {
            WriteLogEvent("error", "gate.console_session");
            return ConsoleSessionExitCode;
        }

        WriteLogEvent("information", "gate.child_session");
        ShellBootstrap.StartExplorer();
        WriteLogEvent("information", "shell.started");

        // M0 keeps the agent alive for the lifetime of the session. The host claim and the
        // unclaimed timeout arrive together with the named-pipe server in M2.
        using ManualResetEventSlim agentLifetime = new(false);
        agentLifetime.Wait();
        return 0;
    }

    private static bool IsExpectedStartFailure(Exception error)
    {
        return error is InvalidOperationException
            || error is IOException
            || error is Win32Exception
            || error is UnauthorizedAccessException;
    }

    private static void WriteLogEvent(string level, string eventName)
    {
        AgentLogLine line = new(
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            level,
            eventName,
            Environment.ProcessId);
        Console.Error.WriteLine(JsonSerializer.Serialize(line));
    }
}

/// <summary>
/// One structured log line. The field set is the log whitelist from the interface contract:
/// no window titles, typed text, clipboard content, account names or paths ever go in here.
/// </summary>
internal sealed record AgentLogLine(
    [property: JsonPropertyName("ts")] string Timestamp,
    [property: JsonPropertyName("level")] string Level,
    [property: JsonPropertyName("event")] string EventName,
    [property: JsonPropertyName("pid")] int ProcessId);