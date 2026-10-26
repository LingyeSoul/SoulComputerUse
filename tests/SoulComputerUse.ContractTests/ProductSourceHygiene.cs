using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace SoulComputerUse.ContractTests;

/// <summary>
/// Scans the product sources for names that would break two promises behaviour tests cannot fully
/// prove: that the connection is never configured with a credential, and that connection settings are
/// never written to disk.
/// </summary>
/// <remarks>
/// This lives in the test assembly on purpose. It is a development-time check: the shipped product has
/// no source tree to scan and must not carry an entry point that reads files from a caller-given path.
/// </remarks>
internal static class ProductSourceHygiene
{
    /// <summary>Names that would mean the connection is configured with a credential.</summary>
    internal static readonly string[] ForbiddenCredentialMembers =
    [
        "ClearTextPassword",
        "SaveCredentials",
        "Password",
    ];

    /// <summary>Names that would mean connection settings are written to disk.</summary>
    internal static readonly string[] ForbiddenPersistenceMembers =
    [
        "Export",
        "SaveAs",
        "SaveSettings",
    ];

    /// <summary>Names that would mean the control is told who to log in as, bypassing the dialog.</summary>
    internal static readonly string[] ForbiddenIdentityMembers =
    [
        "UserName",
        "Domain",
    ];

    /// <summary>
    /// Lists where a forbidden name appears in the sources under <paramref name="sourceDirectory"/>.
    /// A missing directory is an error, never an empty result: "scanned nothing" must not read as
    /// "found nothing".
    /// </summary>
    internal static IReadOnlyList<string> FindForbiddenUsage(string sourceDirectory, string selfFileName)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceDirectory);
        ArgumentException.ThrowIfNullOrEmpty(selfFileName);

        if (!Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException(
                "The source directory to scan does not exist, so the hygiene check would silently pass by scanning nothing.");
        }

        List<string> violations = new();
        foreach (string filePath in Directory.EnumerateFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories))
        {
            if (filePath.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || filePath.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // The scanner declares the forbidden names as literals, so it would always report itself.
            if (string.Equals(Path.GetFileName(filePath), selfFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string[] lines = ReadAllLinesWithRetry(filePath);
            for (int index = 0; index < lines.Length; index++)
            {
                if (IsComment(lines[index]))
                {
                    continue;
                }

                foreach (string forbidden in ForbiddenCredentialMembers.Concat(ForbiddenPersistenceMembers).Concat(ForbiddenIdentityMembers))
                {
                    if (Regex.IsMatch(lines[index], $@"\b{Regex.Escape(forbidden)}\b", RegexOptions.CultureInvariant))
                    {
                        violations.Add($"{Path.GetFileName(filePath)}:{index + 1}: {forbidden}");
                    }
                }
            }
        }

        return violations;
    }

    /// <summary>Locates the product source directory by walking up from the test output directory.</summary>
    internal static string LocateSourceDirectory()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            string candidate = Path.Combine(current.FullName, "src");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            "The src directory could not be found from the test output directory, so the hygiene check cannot run.");
    }

    private static bool IsComment(string line)
    {
        string trimmed = line.TrimStart();
        return trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith('*');
    }

    /// <summary>
    /// Reads one source file, retrying briefly while another process is writing it. A source scan runs
    /// against a tree other tools may be editing, and a failed read must surface as a clear error
    /// rather than as an empty scan that looks like "nothing found".
    /// </summary>
    private static string[] ReadAllLinesWithRetry(string filePath)
    {
        const int Attempts = 5;
        const int DelayMilliseconds = 100;

        IOException? lastError = null;
        for (int attempt = 1; attempt <= Attempts; attempt++)
        {
            try
            {
                return File.ReadAllLines(filePath);
            }
            catch (IOException error)
            {
                lastError = error;
                Thread.Sleep(DelayMilliseconds);
            }
        }

        throw new IOException(
            $"Reading '{Path.GetFileName(filePath)}' failed after {Attempts} attempts, so the hygiene scan could not be completed: {lastError?.Message}",
            lastError);
    }
}