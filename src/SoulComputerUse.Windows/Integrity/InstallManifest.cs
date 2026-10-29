using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace SoulComputerUse.Windows.Integrity;

/// <summary>What comparing one launched executable with the installation manifest established.</summary>
public enum ExecutableIntegrity
{
    /// <summary>The manifest lists the executable and the file's SHA-256 hash matches the recorded hash.</summary>
    Verified = 0,

    /// <summary>
    /// No installation manifest sits next to the executables, so this is a source or test build. The
    /// installed layout ships the manifest in the same administrator-protected directory as the binaries,
    /// so an unprivileged attacker cannot remove it to fall back to this status.
    /// </summary>
    NotInstalled = 1,

    /// <summary>The manifest exists but is unreadable, is not valid JSON, or does not follow the manifest contract.</summary>
    ManifestUnreadable = 2,

    /// <summary>The manifest lists files, but not this executable.</summary>
    NotListed = 3,

    /// <summary>The manifest lists this executable, but the file is absent.</summary>
    FileMissing = 4,

    /// <summary>The manifest lists this executable, but the file's SHA-256 hash differs from the recorded hash.</summary>
    Mismatch = 5,

    /// <summary>The manifest lists this executable, but the file could not be read.</summary>
    Unreadable = 6,
}

/// <summary>
/// Verifies a launched executable against the SHA-256 hashes in <c>install-manifest.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// Only <see cref="ExecutableIntegrity.Verified"/> and <see cref="ExecutableIntegrity.NotInstalled"/> may
/// continue into a launch with elevated or host authority; every other status is a refusal. NotInstalled
/// is allowed because a source or test build has no manifest, and in the installed layout the manifest
/// sits beside the binaries in an administrator-protected directory, so an unprivileged attacker cannot
/// delete it to make the check fall back to that status.
/// </para>
/// <para>
/// The status cases never throw: an absent manifest, an unreadable manifest, invalid JSON, a wrong
/// version or algorithm, an absent target file and an unreadable target file all return a status. Only a
/// null or whitespace argument throws.
/// </para>
/// </remarks>
public static class InstallManifest
{
    /// <summary>The manifest file name, next to this program and the executables it verifies.</summary>
    public const string FileName = "install-manifest.json";

    private const int SupportedManifestVersion = 1;
    private const string SupportedAlgorithm = "SHA-256";
    private const string ManifestVersionProperty = "manifestVersion";
    private const string AlgorithmProperty = "algorithm";
    private const string FilesProperty = "files";

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 16,
    };

    /// <summary>Verifies an executable against the manifest beside this program.</summary>
    /// <param name="executablePath">The executable to verify; only its file name is looked up in the manifest.</param>
    /// <returns>The comparison status.</returns>
    /// <exception cref="ArgumentException">The path is null, empty or whitespace.</exception>
    public static ExecutableIntegrity Verify(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        return Verify(executablePath, Path.Combine(AppContext.BaseDirectory, FileName));
    }

    /// <summary>Verifies an executable against an explicit manifest.</summary>
    /// <param name="executablePath">The executable to verify; only its file name is looked up in the manifest.</param>
    /// <param name="manifestPath">The manifest to read, which lets tests and tools verify a copied layout.</param>
    /// <returns>The comparison status.</returns>
    /// <exception cref="ArgumentException">A path is null, empty or whitespace.</exception>
    public static ExecutableIntegrity Verify(string executablePath, string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);

        if (!File.Exists(manifestPath))
        {
            return ExecutableIntegrity.NotInstalled;
        }

        string? fileName = Path.GetFileName(executablePath);
        if (string.IsNullOrEmpty(fileName))
        {
            return ExecutableIntegrity.NotListed;
        }

        string? expectedHash;
        try
        {
            expectedHash = ReadExpectedHash(manifestPath, fileName);
        }
        catch (Exception error) when (IsUnreadableManifestError(error))
        {
            return ExecutableIntegrity.ManifestUnreadable;
        }

        if (expectedHash is null)
        {
            return ExecutableIntegrity.NotListed;
        }

        if (!File.Exists(executablePath))
        {
            return ExecutableIntegrity.FileMissing;
        }

        byte[] actualHash;
        try
        {
            using FileStream stream = new(executablePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            actualHash = SHA256.HashData(stream);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return ExecutableIntegrity.Unreadable;
        }

        return string.Equals(
            Convert.ToHexStringLower(actualHash),
            expectedHash,
            StringComparison.OrdinalIgnoreCase)
            ? ExecutableIntegrity.Verified
            : ExecutableIntegrity.Mismatch;
    }

    private static bool IsUnreadableManifestError(Exception error)
    {
        return error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException
            or NotSupportedException or ArgumentException;
    }

    /// <summary>Reads the recorded hash for one file, or null when the manifest does not list it.</summary>
    /// <exception cref="InvalidDataException">The document does not follow the manifest contract.</exception>
    /// <exception cref="JsonException">The document is not valid JSON.</exception>
    /// <exception cref="IOException">The document could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The document could not be opened.</exception>
    private static string? ReadExpectedHash(string manifestPath, string fileName)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(manifestPath), DocumentOptions);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The installation manifest is not a JSON object.");
        }

        if (!root.TryGetProperty(ManifestVersionProperty, out JsonElement version)
            || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out int versionNumber)
            || versionNumber != SupportedManifestVersion)
        {
            throw new InvalidDataException("The installation manifest does not declare the supported version.");
        }

        if (!root.TryGetProperty(AlgorithmProperty, out JsonElement algorithm)
            || algorithm.ValueKind != JsonValueKind.String
            || !string.Equals(algorithm.GetString(), SupportedAlgorithm, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The installation manifest does not declare the supported hash algorithm.");
        }

        if (!root.TryGetProperty(FilesProperty, out JsonElement files) || files.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The installation manifest does not declare a files object.");
        }

        string? expectedHash = null;
        foreach (JsonProperty file in files.EnumerateObject())
        {
            if (file.Value.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("The installation manifest declares a file hash that is not a string.");
            }

            if (string.Equals(file.Name, fileName, StringComparison.OrdinalIgnoreCase))
            {
                expectedHash = file.Value.GetString();
            }
        }

        return expectedHash;
    }
}
