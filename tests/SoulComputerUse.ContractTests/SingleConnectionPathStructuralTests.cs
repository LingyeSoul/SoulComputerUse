using System;
using System.Collections.Generic;
using System.IO;

namespace SoulComputerUse.ContractTests;

/// <summary>
/// Structural gate for the single connection entry point of section 7.2: asking the hosted control to
/// connect may happen in exactly one file, so no later change can add a second route that skips the
/// leftover-child-session sweep.
/// </summary>
/// <remarks>
/// This is the layer a functional case cannot reach: it fails on code that never runs, which is exactly
/// the shape a second connect path takes while it waits for somebody to call it.
/// </remarks>
[TestClass]
public sealed class SingleConnectionPathStructuralTests
{
    /// <summary>The repository marker the scan anchors on, so the working directory does not matter.</summary>
    private const string RepositoryMarker = "SoulComputerUse.slnx";

    /// <summary>The one file allowed to ask the hosted control to connect.</summary>
    private const string EntryPointFile = "src/SoulComputerUse.Windows/Rdp/ChildSessionConnection.cs";

    /// <summary>A call that starts a connection on the hosted control.</summary>
    private const string ControlConnectCall = ".Connect(";

    /// <summary>A call to the sanctioned entry point, which any file may make.</summary>
    private const string EntryPointCall = "ChildSessionConnection.Connect(";

    /// <summary>The lowest number of sources a meaningful scan can read.</summary>
    private const int MinimumSourcesToRead = 10;

    [TestMethod]
    public void TheControlsConnectCallAppearsInTheEntryPointFileAndNowhereElse()
    {
        string root = FindRepositoryRoot();
        string entryPoint = Path.Combine(root, EntryPointFile.Replace('/', Path.DirectorySeparatorChar));

        Assert.IsTrue(
            File.Exists(entryPoint),
            $"The entry point file {EntryPointFile} is missing, so nothing about the connection path can be proved.");

        List<string> sources = CollectSources(Path.Combine(root, "src"));
        Assert.IsGreaterThanOrEqualTo(
            MinimumSourcesToRead,
            sources.Count,
            $"Only {sources.Count} source files were read, so a clean scan would prove nothing about the tree.");

        List<string> violations = [];
        int entryPointCalls = 0;
        foreach (string source in sources)
        {
            int calls = CountControlConnectCalls(File.ReadAllText(source));
            if (calls == 0)
            {
                continue;
            }

            if (string.Equals(source, entryPoint, StringComparison.OrdinalIgnoreCase))
            {
                entryPointCalls += calls;
            }
            else
            {
                violations.Add($"{Relative(root, source)} ({calls})");
            }
        }

        Assert.IsGreaterThanOrEqualTo(
            1,
            entryPointCalls,
            $"The entry point file {EntryPointFile} never asks the control to connect, so this rule would also pass on a tree that cannot connect at all.");

        Assert.IsEmpty(
            violations,
            "A second place asks the hosted control to connect (" + string.Join(", ", violations) + "). The leftover-child-session sweep lives on the single entry point, so any other connect route can hand over a session that never asked the operator to log on. Route it through the entry point, or delete it.");
    }

    [TestMethod]
    public void TheOnlyConnectCallerFeedsTheObservedStatesIntoTheLogonGate()
    {
        string root = FindRepositoryRoot();
        string wireFile = Path.Combine(
            root,
            "src/SoulComputerUse.RdpCredentialExperiment/ConnectRun.cs".Replace('/', Path.DirectorySeparatorChar));

        Assert.IsTrue(
            File.Exists(wireFile),
            "The only caller of the connect entry point is missing, so whether the logon gate really runs cannot be proved.");

        string wireSource = File.ReadAllText(wireFile);
        Assert.Contains(
            "ChildSessionConnection.Observe(",
            wireSource,
            "The connect run observes the client state without feeding it into the logon gate, so a connection that jumps straight to ready would be logged as a pass instead of failing.");
    }

    /// <summary>
    /// Counts the control's connect calls on code lines. Calls to the entry point do not count, and
    /// comments are skipped, because prose is allowed to name the call this rule forbids.
    /// </summary>
    private static int CountControlConnectCalls(string text)
    {
        int calls = 0;
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('*'))
            {
                continue;
            }

            calls += CountOccurrences(line, ControlConnectCall) - CountOccurrences(line, EntryPointCall);
        }

        return calls;
    }

    private static int CountOccurrences(string text, string needle)
    {
        int count = 0;
        int index = text.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    private static List<string> CollectSources(string sourceRoot)
    {
        List<string> sources = [];
        if (!Directory.Exists(sourceRoot))
        {
            return sources;
        }

        foreach (string candidate in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(candidate))
            {
                continue;
            }

            sources.Add(candidate);
        }

        return sources;
    }

    private static bool IsBuildOutput(string path)
    {
        return path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Finds the repository root by walking up from the test binary. Failure messages only ever carry
    /// relative paths, so no machine path can leak into a report.
    /// </summary>
    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, RepositoryMarker)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new AssertFailedException(
            $"Could not find {RepositoryMarker} above the test binary, so the connection path cannot be checked.");
    }

    private static string Relative(string root, string path)
    {
        return Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
    }
}