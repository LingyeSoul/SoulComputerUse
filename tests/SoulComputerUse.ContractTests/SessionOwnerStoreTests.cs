using System;
using System.IO;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.SingleInstance;

namespace SoulComputerUse.ContractTests;

/// <summary>
/// The ownership record's persistence fence: a closing record is terminal for every writer except a
/// takeover that already holds the machine-wide lock, a store that released its record cannot write it
/// back, and a corrupt record is still refused instead of being overwritten.
/// </summary>
/// <remarks>
/// Every case runs against its own temporary root, so no case can read or write the record of a real
/// session.
/// </remarks>
[TestClass]
public sealed class SessionOwnerStoreTests
{
    private const string TestSid = "S-1-5-21-0000000000-0000000000-0000000000-1001";
    private const string TestSessionId = "b3f1c2d4-0000-4000-8000-000000000000";

    [TestMethod]
    public void AClosingRecordOnDiskIsNotOverwrittenByReady()
    {
        using TemporaryDirectory root = new();
        SessionOwnerStore writer = new(root.Path, TestSid);
        writer.Write(CreateRecord(SessionState.Closing));

        SessionOwnerStore reader = new(root.Path, TestSid);
        Assert.ThrowsExactly<InvalidOperationException>(
            () => reader.Write(CreateRecord(SessionState.Ready)));

        SessionOwnerRecord? readBack = reader.Read();
        Assert.IsNotNull(readBack);
        Assert.AreEqual(SessionState.Closing, readBack.State, "The closing record must stay on disk untouched.");
    }

    [TestMethod]
    public void AStoreThatDeclaredClosingDoesNotWriteReadyAgain()
    {
        using TemporaryDirectory root = new();
        SessionOwnerStore store = new(root.Path, TestSid);
        store.Write(CreateRecord(SessionState.Closing));

        Assert.ThrowsExactly<InvalidOperationException>(
            () => store.Write(CreateRecord(SessionState.Ready)));

        SessionOwnerRecord? readBack = store.Read();
        Assert.IsNotNull(readBack);
        Assert.AreEqual(SessionState.Closing, readBack.State, "The runtime observer must not reopen a closing session.");
    }

    [TestMethod]
    public void AClosingRecordAcceptsAnotherClosingWrite()
    {
        using TemporaryDirectory root = new();
        SessionOwnerStore store = new(root.Path, TestSid);
        SessionOwnerRecord closing = CreateRecord(SessionState.Closing);
        store.Write(closing);

        SessionOwnerRecord later = closing with { AcquiredAt = closing.AcquiredAt.AddMinutes(1) };
        store.Write(later);

        SessionOwnerRecord? readBack = store.Read();
        Assert.IsNotNull(readBack);
        Assert.AreEqual(later, readBack, "A closing write is the only state that may replace a closing record.");
    }

    [TestMethod]
    public void AClosingRecordAcceptsTheTakeoverStartingWrite()
    {
        using TemporaryDirectory root = new();
        SessionOwnerStore previousOwner = new(root.Path, TestSid);
        previousOwner.Write(CreateRecord(SessionState.Closing));

        SessionOwnerStore nextOwner = new(root.Path, TestSid);
        nextOwner.Write(CreateRecord(SessionState.Starting));

        SessionOwnerRecord? readBack = nextOwner.Read();
        Assert.IsNotNull(readBack);
        Assert.AreEqual(
            SessionState.Starting,
            readBack.State,
            "A start holds the machine-wide lock before it writes 'starting', so replacing a dead owner's closing record is a takeover, not a resurrection.");
    }

    [TestMethod]
    public void AStoreThatWroteAndDeletedItsRecordRefusesToWriteAgain()
    {
        using TemporaryDirectory root = new();
        SessionOwnerStore store = new(root.Path, TestSid);
        store.Write(CreateRecord(SessionState.Closing));
        store.Delete();

        Assert.ThrowsExactly<InvalidOperationException>(
            () => store.Write(CreateRecord(SessionState.Ready)));

        Assert.IsFalse(File.Exists(store.RecordPath), "A released store must not recreate the record.");
    }

    [TestMethod]
    public void AStoreThatOnlyDeletedATakeoverLeftoverStaysUsable()
    {
        using TemporaryDirectory root = new();
        SessionOwnerStore previousOwner = new(root.Path, TestSid);
        previousOwner.Write(CreateRecord(SessionState.Disconnected));

        SessionOwnerStore nextOwner = new(root.Path, TestSid);
        SessionOwnerRecord? leftover = nextOwner.Read();
        Assert.IsNotNull(leftover, "The takeover has to read the previous owner's record before deleting it.");
        nextOwner.Delete();
        nextOwner.Write(CreateRecord(SessionState.Starting));

        SessionOwnerRecord? readBack = nextOwner.Read();
        Assert.IsNotNull(readBack);
        Assert.AreEqual(
            SessionState.Starting,
            readBack.State,
            "A takeover delete removes another owner's leftover and must not refuse the replacement record.");
    }

    [TestMethod]
    public void ACorruptRecordIsRefusedInsteadOfBeingOverwritten()
    {
        using TemporaryDirectory root = new();
        SessionOwnerStore store = new(root.Path, TestSid);
        const string corrupt = "{ this is not json";
        WriteRawRecord(store, corrupt);

        Assert.ThrowsExactly<InvalidOperationException>(
            () => store.Write(CreateRecord(SessionState.Ready)));

        Assert.AreEqual(
            corrupt,
            File.ReadAllText(store.RecordPath),
            "A corrupt record is evidence for the operator and must not be overwritten.");
    }

    [TestMethod]
    public void AWriteAfterWriteStillReplacesTheRecord()
    {
        using TemporaryDirectory root = new();
        SessionOwnerStore store = new(root.Path, TestSid);
        store.Write(CreateRecord(SessionState.Connecting));
        store.Write(CreateRecord(SessionState.Ready));

        SessionOwnerRecord? readBack = store.Read();
        Assert.IsNotNull(readBack);
        Assert.AreEqual(SessionState.Ready, readBack.State, "Ordinary state progress must still be written.");
    }

    private static void WriteRawRecord(SessionOwnerStore store, string payload)
    {
        string directory = Path.GetDirectoryName(store.RecordPath)
            ?? throw new InvalidOperationException("The test record path has no directory part.");
        Directory.CreateDirectory(directory);
        File.WriteAllText(store.RecordPath, payload);
    }

    private static SessionOwnerRecord CreateRecord(SessionState state)
    {
        return new SessionOwnerRecord(
            TestSessionId,
            Environment.ProcessId,
            new DateTimeOffset(2026, 10, 25, 14, 0, 0, TimeSpan.Zero),
            3,
            ChildSessionsOpenedByUs: true,
            state,
            new DateTimeOffset(2026, 10, 25, 14, 0, 1, TimeSpan.Zero));
    }
}
