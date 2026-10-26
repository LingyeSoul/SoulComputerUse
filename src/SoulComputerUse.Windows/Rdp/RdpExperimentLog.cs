using System;
using System.IO;

namespace SoulComputerUse.Windows.Rdp;

/// <summary>
/// Writes the outcome of one credential experiment step to a log file, so a crash inside the remote
/// desktop control still leaves evidence of how far the run got.
/// </summary>
/// <remarks>
/// Only step names, booleans and error codes go into the log. Credential values never do: the
/// contract promises that application code neither reads nor persists them.
/// </remarks>
public static class RdpExperimentLog
{
    /// <summary>Appends one line to the experiment log.</summary>
    public static void Append(string logPath, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        string? directory = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrEmpty(directory))
        {
            _ = Directory.CreateDirectory(directory);
        }

        File.AppendAllText(logPath, message + Environment.NewLine);
    }

    /// <summary>Records one step with its result.</summary>
    public static void Step(string logPath, string step, bool succeeded, string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(step);
        Append(logPath, $"{step}: {(succeeded ? "ok" : "failed")}{((detail.Length == 0) ? string.Empty : " · " + detail)}");
    }
}
