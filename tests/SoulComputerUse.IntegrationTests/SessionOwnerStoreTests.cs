using System;
using System.IO;
using System.Text.Json;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.SingleInstance;

namespace SoulComputerUse.IntegrationTests;

/// <summary>
/// The per-user ownership record against the real file system: the path is split per SID, the
/// seven contract fields survive a round trip, and a corrupt record is refused instead of being
/// deleted, because a corrupt record is evidence the operator has to look at.
/// </summary>
[TestClass]
public sealed class SessionOwnerStoreTests
{
    private const string TestSid = "S-1-5-21-0000000000-0000000000-0000000000-1001";
    private const string TestSessionId = "b3f1c2d4-0000-4000-8000-000000000000";

    private string _root = string.Empty;

    [TestInitialize]
    public void CreateTemporaryRoot()
    {
        _root = Path.Combine(Path.GetTempPath(), "scu-owner-" + Guid.NewGuid().ToString("N"));
    }

    [TestCleanup]
    public void DeleteTemporaryRoot()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public void RecordPathIsSplitPerUserSid()
    {
        SessionOwnerStore store = new(_root, TestSid);

        Assert.AreEqual(
            Path.Combine(_root, "SoulComputerUse", $"owner-{TestSid}.json"),
            store.RecordPath,
            "Each Windows account must get its own record file.");
    }

    [TestMethod]
    public void WriteThenReadRoundTripsEveryContractField()
    {
        SessionOwnerStore store = new(_root, TestSid);
        SessionOwnerRecord record = CreateSampleRecord();

        store.Write(record);
        SessionOwnerRecord? readBack = store.Read();

        Assert.IsNotNull(readBack);
        Assert.AreEqual(record, readBack, "Every contract field must survive the round trip.");
    }

    [TestMethod]
    public void WrittenRecordUsesTheContractFieldNames()
    {
        SessionOwnerStore store = new(_root, TestSid);
        store.Write(CreateSampleRecord());

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(store.RecordPath));
        JsonElement root = document.RootElement;

        Assert.IsTrue(root.TryGetProperty("sessionId", out _), "The record must carry sessionId.");
        Assert.IsTrue(root.TryGetProperty("hostPid", out _), "The record must carry hostPid.");
        Assert.IsTrue(root.TryGetProperty("hostProcessStartTime", out _), "The record must carry hostProcessStartTime.");
        Assert.IsTrue(root.TryGetProperty("subagentWtsSessionId", out _), "The record must carry subagentWtsSessionId.");
        Assert.IsTrue(root.TryGetProperty("childSessionsOpenedByUs", out _), "The record must carry childSessionsOpenedByUs.");
        Assert.IsTrue(root.TryGetProperty("acquiredAt", out _), "The record must carry acquiredAt.");
        Assert.AreEqual(
            "connecting",
            root.GetProperty("state").GetString(),
            "The state must be written with its contract wire name.");
    }

    [TestMethod]
    public void MissingRecordReadsAsNull()
    {
        SessionOwnerStore store = new(_root, TestSid);

        Assert.IsNull(store.Read(), "No record means a normal start, not an error.");
    }

    [TestMethod]
    public void EmptyJsonObjectIsRefusedInsteadOfBecomingADefaultRecord()
    {
        SessionOwnerStore store = new(_root, TestSid);
        WriteRawRecord(store, "{}");

        Assert.ThrowsExactly<InvalidOperationException>(() => store.Read());
        Assert.IsTrue(File.Exists(store.RecordPath), "An unusable record must be kept for inspection.");
    }

    [TestMethod]
    public void RecordWithZeroedFieldsIsRefused()
    {
        SessionOwnerStore store = new(_root, TestSid);
        WriteRawRecord(
            store,
            """
            {
              "sessionId": "",
              "hostPid": 0,
              "hostProcessStartTime": "0001-01-01T00:00:00+00:00",
              "subagentWtsSessionId": 0,
              "childSessionsOpenedByUs": false,
              "state": "starting",
              "acquiredAt": "0001-01-01T00:00:00+00:00"
            }
            """);

        Assert.ThrowsExactly<InvalidOperationException>(
            () => store.Read());
    }

    [TestMethod]
    public void CorruptRecordIsRefusedAndKept()
    {
        SessionOwnerStore store = new(_root, TestSid);
        WriteRawRecord(store, "{ this is not json");

        Assert.ThrowsExactly<InvalidOperationException>(() => store.Read());
        Assert.IsTrue(
            File.Exists(store.RecordPath),
            "A corrupt record is evidence for the operator and must not be deleted silently.");
    }

    [TestMethod]
    public void DeleteIsIdempotent()
    {
        SessionOwnerStore store = new(_root, TestSid);
        store.Write(CreateSampleRecord());

        store.Delete();
        store.Delete();

        Assert.IsFalse(File.Exists(store.RecordPath), "Deleting twice must leave no record behind.");
    }

    private static void WriteRawRecord(SessionOwnerStore store, string payload)
    {
        string directory = Path.GetDirectoryName(store.RecordPath)
            ?? throw new InvalidOperationException("The test record path has no directory part.");
        Directory.CreateDirectory(directory);
        File.WriteAllText(store.RecordPath, payload);
    }

    private static SessionOwnerRecord CreateSampleRecord()
    {
        return new SessionOwnerRecord(
            TestSessionId,
            Environment.ProcessId,
            new DateTimeOffset(2026, 10, 25, 14, 0, 0, TimeSpan.Zero),
            3,
            ChildSessionsOpenedByUs: true,
            SessionState.Connecting,
            new DateTimeOffset(2026, 10, 25, 14, 0, 1, TimeSpan.Zero));
    }
}