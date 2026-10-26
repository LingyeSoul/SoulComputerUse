using System;
using System.Collections.Generic;
using System.IO;

namespace SoulComputerUse.ContractTests;

/// <summary>
/// Source-level hygiene for the two promises behaviour tests cannot fully prove: this product never
/// configures a connection with a credential, and never writes connection settings to disk. The scan
/// runs against the real <c>src</c> tree, so a forbidden name fails the suite the moment it is added.
/// </summary>
[TestClass]
public sealed class ProductSourceHygieneTests
{
    private const string ScannerFileName = "ProductSourceHygiene.cs";

    [TestMethod]
    public void ProductionSourcesNeverNameACredentialOrASavedConnection()
    {
        IReadOnlyList<string> violations = ProductSourceHygiene.FindForbiddenUsage(
            ProductSourceHygiene.LocateSourceDirectory(),
            ScannerFileName);

        Assert.IsEmpty(
            violations,
            $"The product must never fill in a credential or save connection settings; found: {string.Join("; ", violations)}");
    }

    [TestMethod]
    public void AWrongPathIsAnErrorNotAPass()
    {
        Assert.ThrowsExactly<DirectoryNotFoundException>(
            () => ProductSourceHygiene.FindForbiddenUsage(
                Path.Combine(Path.GetTempPath(), "scu-missing-" + Guid.NewGuid().ToString("N")),
                ScannerFileName));
    }

    [TestMethod]
    public void TheScanActuallyDetectsAForbiddenName()
    {
        using TemporarySourceDirectory source = new("Sample.cs", "var client = ocx; client.ClearTextPassword = \"x\";");

        IReadOnlyList<string> violations = ProductSourceHygiene.FindForbiddenUsage(source.Path, ScannerFileName);

        Assert.HasCount(1, violations, "A scan that cannot detect a forbidden name proves nothing about the real tree.");
        StringAssert.Contains(violations[0], "ClearTextPassword");
    }

    [TestMethod]
    public void CommentsAreNotTreatedAsUsage()
    {
        using TemporarySourceDirectory source = new("Sample.cs", "// the control must never be given a Password or ClearTextPassword\nvar ok = 1;");

        Assert.IsEmpty(
            ProductSourceHygiene.FindForbiddenUsage(source.Path, ScannerFileName),
            "Explaining why a name is forbidden must not itself count as using it.");
    }

    [TestMethod]
    public void TheScannerDoesNotReportItsOwnDeclarationList()
    {
        using TemporarySourceDirectory source = new(
            ScannerFileName,
            "internal static readonly string[] Forbidden = [\"ClearTextPassword\"];");

        Assert.IsEmpty(
            ProductSourceHygiene.FindForbiddenUsage(source.Path, ScannerFileName),
            "The scanner declares the forbidden names itself, so it has to exclude its own file rather than always failing.");
    }

    /// <summary>A temporary one-file source directory that deletes itself.</summary>
    private sealed class TemporarySourceDirectory : IDisposable
    {
        internal TemporarySourceDirectory(string fileName, string content)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "scu-hygiene-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            File.WriteAllText(System.IO.Path.Combine(Path, fileName), content);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}