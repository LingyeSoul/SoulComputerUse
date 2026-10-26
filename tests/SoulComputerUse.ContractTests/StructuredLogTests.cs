using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Logging;

namespace SoulComputerUse.ContractTests;

/// <summary>
/// Contract tests for the structured log layer of section 10: the fixed event set, the eleven
/// whitelisted fields, the JSON Lines encoding and every rejection rule.
/// </summary>
[TestClass]
public sealed class StructuredLogTests
{
    private const string FixedSessionId = "1f2e3d4c-5b6a-4798-8a9b-0c1d2e3f4a5b";

    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 10, 26, 12, 34, 56, TimeSpan.Zero);

    private static readonly string[] WhitelistedFieldNames =
    [
        "Timestamp",
        "Level",
        "Event",
        "SessionId",
        "State",
        "Code",
        "Method",
        "DurationMs",
        "Pid",
        "Width",
        "Height",
    ];

    private static readonly string[] WhitelistedWireNames =
    [
        "ts",
        "level",
        "event",
        "sessionId",
        "state",
        "code",
        "method",
        "durationMs",
        "pid",
        "width",
        "height",
    ];

    private static readonly string[] MinimalWireNames = ["ts", "level", "event"];

    [TestMethod]
    public void EveryEventHasAUniqueLowercaseDotSeparatedWireNameThatRoundTrips()
    {
        LogEvent[] events = Enum.GetValues<LogEvent>();

        Assert.HasCount(19, events, "The contract's event set is fixed, so its size must not drift silently.");

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (LogEvent logEvent in events)
        {
            string wireName = LogEventNames.ToWireName(logEvent);

            Assert.IsTrue(seen.Add(wireName), $"The wire name '{wireName}' is assigned to more than one event.");
            Assert.AreEqual(wireName.ToLowerInvariant(), wireName, "A wire name must be lowercase.");
            Assert.Contains(".", wireName, "A wire name must be dot-separated.");
            Assert.IsTrue(LogEventNames.TryFromWireName(wireName, out LogEvent parsed), "A wire name must parse back.");
            Assert.AreEqual(logEvent, parsed, "A wire name must round-trip to its own event.");
        }

        Assert.HasCount(events.Length, seen, "Every event needs its own wire name.");
    }

    [TestMethod]
    public void EveryContractEventNameIsPresent()
    {
        string[] contractNames =
        [
            "lock.acquired",
            "lock.released",
            "lock.takeover",
            "session.starting",
            "session.ready",
            "session.blocked",
            "session.closing",
            "session.disconnected",
            "childsessions.enabled",
            "childsessions.restored",
            "agent.spawned",
            "agent.claimed",
            "agent.unclaimed",
            "agent.dead",
            "ipc.peer_refused",
            "ipc.protocol_error",
            "teardown.failed",
            "gate.tier_selected",
            "gate.tier_none",
        ];

        foreach (string contractName in contractNames)
        {
            Assert.IsTrue(
                LogEventNames.TryFromWireName(contractName, out LogEvent parsed),
                $"The contract event '{contractName}' must be defined.");
            Assert.AreEqual(contractName, LogEventNames.ToWireName(parsed), "The wire name must be the contract name itself.");
        }
    }

    [TestMethod]
    public void AnUnknownWireNameIsRefusedInsteadOfFallingBackToADefaultEvent()
    {
        Assert.IsFalse(LogEventNames.TryFromWireName("session.exploded", out LogEvent parsed), "An invented event must not parse.");
        Assert.AreEqual(default(LogEvent), parsed, "A failed parse returns the default value, which the caller must not log.");

        Assert.IsFalse(LogEventNames.TryFromWireName(null, out _), "A null wire name must not parse.");
        Assert.IsFalse(LogEventNames.TryFromWireName("LOCK.ACQUIRED", out _), "Wire names are case-sensitive.");
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => LogEventNames.ToWireName((LogEvent)9999));
    }

    [TestMethod]
    public void EveryLevelHasALowercaseWireNameThatRoundTrips()
    {
        foreach (LogLevel level in Enum.GetValues<LogLevel>())
        {
            string wireName = LogLevelNames.ToWireName(level);

            Assert.AreEqual(wireName.ToLowerInvariant(), wireName, "A level wire name must be lowercase.");
            Assert.IsTrue(LogLevelNames.TryFromWireName(wireName, out LogLevel parsed), "A level wire name must parse back.");
            Assert.AreEqual(level, parsed, "A level wire name must round-trip to its own level.");
        }

        Assert.IsFalse(LogLevelNames.TryFromWireName("verbose", out _), "An invented level must not parse.");
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => LogLevelNames.ToWireName((LogLevel)99));
    }

    [TestMethod]
    public void ACompleteRecordIsEncodedFieldByFieldInTheContractOrder()
    {
        LogRecord record = CreateCompleteRecord();

        string line = LogRecordWriter.Write(record);

        Assert.AreEqual(
            "{\"ts\":\"2026-10-26T12:34:56.0000000+00:00\","
                + "\"level\":\"information\","
                + "\"event\":\"session.ready\","
                + "\"sessionId\":\"" + FixedSessionId + "\","
                + "\"state\":\"ready\","
                + "\"code\":\"E_NOT_READY\","
                + "\"method\":\"screenshot\","
                + "\"durationMs\":42,"
                + "\"pid\":1234,"
                + "\"width\":1920,"
                + "\"height\":1080}",
            line,
            "The encoded line must carry exactly the eleven whitelisted fields, in the contract order.");
        Assert.IsFalse(line.Contains('\n', StringComparison.Ordinal), "One record is one line.");
        Assert.IsFalse(line.Contains('\r', StringComparison.Ordinal), "One record is one line.");
    }

    [TestMethod]
    public void TheEncodedLineHasExactlyTheWhitelistedKeysWhateverTheirOrder()
    {
        string line = LogRecordWriter.Write(CreateCompleteRecord());

        using JsonDocument document = JsonDocument.Parse(line);
        JsonElement root = document.RootElement;
        Assert.AreEqual(JsonValueKind.Object, root.ValueKind);

        List<string> actualKeys = root.EnumerateObject().Select(property => property.Name).ToList();
        CollectionAssert.AreEquivalent(WhitelistedWireNames, actualKeys, "The key set is the contract whitelist, nothing more.");

        Assert.AreEqual("information", root.GetProperty("level").GetString());
        Assert.AreEqual("session.ready", root.GetProperty("event").GetString());
        Assert.AreEqual(FixedSessionId, root.GetProperty("sessionId").GetString());
        Assert.AreEqual("ready", root.GetProperty("state").GetString());
        Assert.AreEqual("E_NOT_READY", root.GetProperty("code").GetString());
        Assert.AreEqual("screenshot", root.GetProperty("method").GetString());
        Assert.AreEqual(42, root.GetProperty("durationMs").GetInt32());
        Assert.AreEqual(1234, root.GetProperty("pid").GetInt32());
        Assert.AreEqual(1920, root.GetProperty("width").GetInt32());
        Assert.AreEqual(1080, root.GetProperty("height").GetInt32());

        DateTimeOffset timestamp = DateTimeOffset.Parse(
            root.GetProperty("ts").GetString()!,
            CultureInfo.InvariantCulture);
        Assert.AreEqual(FixedTimestamp, timestamp, "The timestamp must round-trip as the same instant.");
        Assert.AreEqual(TimeSpan.Zero, timestamp.Offset, "The timestamp must be written in UTC.");
    }

    [TestMethod]
    public void NullFieldsAreOmittedInsteadOfWrittenAsJsonNull()
    {
        LogRecord record = new(
            FixedTimestamp,
            LogLevel.Warning,
            LogEvent.IpcPeerRefused,
            sessionId: null,
            state: null,
            code: null,
            method: null,
            durationMs: null,
            pid: null,
            width: null,
            height: null);

        string line = LogRecordWriter.Write(record);

        using JsonDocument document = JsonDocument.Parse(line);
        List<string> actualKeys = document.RootElement.EnumerateObject().Select(property => property.Name).ToList();

        CollectionAssert.AreEquivalent(MinimalWireNames, actualKeys, "Only present fields are written.");
        Assert.IsFalse(line.Contains("null", StringComparison.OrdinalIgnoreCase), "An absent field is omitted, never written as null.");
    }

    [TestMethod]
    public void ASessionIdThatIsNotAUuidInDFormatIsRefusedWithoutEchoingIt()
    {
        const string NotAUuid = "..\\..\\AppData\\Local\\owner-record.json";

        ArgumentException error = Assert.ThrowsExactly<ArgumentException>(
            () => LogRecordWriter.Write(CreateRecordWithSessionId(NotAUuid)));

        Assert.IsFalse(
            error.Message.Contains("owner", StringComparison.OrdinalIgnoreCase),
            "The rejection message must not repeat the rejected value.");
        Assert.DoesNotContain("@", error.Message, "The rejection message must not echo a mail-style value.");

        Assert.ThrowsExactly<ArgumentException>(() => CreateRecordWithSessionId("not-a-uuid"));
        Assert.ThrowsExactly<ArgumentException>(() => CreateRecordWithSessionId("{" + FixedSessionId + "}"), "The contract format is 'D', not 'B'.");
        Assert.ThrowsExactly<ArgumentException>(() => CreateRecordWithSessionId(string.Empty));
    }

    [TestMethod]
    public void ANegativeDurationIsRefused()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CreateRecordWithDuration(-1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CreateRecordWithDuration(int.MinValue));
        _ = CreateRecordWithDuration(0);
    }

    [TestMethod]
    public void AProcessIdThatIsNotPositiveIsRefused()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CreateRecordWithPid(0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CreateRecordWithPid(-5));
        _ = CreateRecordWithPid(1);
    }

    [TestMethod]
    public void AScreenBoundThatIsNotPositiveIsRefused()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CreateRecordWithBounds(0, 1080));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CreateRecordWithBounds(1920, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CreateRecordWithBounds(-1, -1));
        _ = CreateRecordWithBounds(1, 1);
    }

    [TestMethod]
    public void AnUndefinedEnumValueIsRefusedOnEveryEnumField()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new LogRecord(
            FixedTimestamp,
            (LogLevel)99,
            LogEvent.SessionReady,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new LogRecord(
            FixedTimestamp,
            LogLevel.Information,
            (LogEvent)99,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new LogRecord(
            FixedTimestamp,
            LogLevel.Information,
            LogEvent.SessionReady,
            null,
            (SessionState)99,
            null,
            null,
            null,
            null,
            null,
            null));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new LogRecord(
            FixedTimestamp,
            LogLevel.Information,
            LogEvent.SessionReady,
            null,
            null,
            (SessionErrorCode)99,
            null,
            null,
            null,
            null,
            null));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new LogRecord(
            FixedTimestamp,
            LogLevel.Information,
            LogEvent.SessionReady,
            null,
            null,
            null,
            (SessionTool)99,
            null,
            null,
            null,
            null));
    }

    [TestMethod]
    public void ATimestampThatIsNotUtcIsRefused()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new LogRecord(
            new DateTimeOffset(2026, 10, 26, 12, 34, 56, TimeSpan.FromHours(8)),
            LogLevel.Information,
            LogEvent.SessionReady,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null));

        Assert.ThrowsExactly<ArgumentException>(() => new LogRecord(
            default,
            LogLevel.Information,
            LogEvent.SessionReady,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null));
    }

    [TestMethod]
    public void TheRecordTypeExposesNoFreeTextMember()
    {
        PropertyInfo[] properties = typeof(LogRecord)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .ToArray();

        string[] actualNames = properties.Select(property => property.Name).ToArray();

        CollectionAssert.AreEqual(
            WhitelistedFieldNames.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            actualNames,
            "A record must expose exactly the eleven whitelisted members, so a member such as Message, Detail, Path or Title cannot be added.");

        foreach (PropertyInfo property in properties)
        {
            Type type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

            Assert.IsTrue(
                type.IsEnum
                    || type == typeof(string)
                    || type == typeof(int)
                    || type == typeof(DateTimeOffset),
                $"'{property.Name}' has type '{property.PropertyType.Name}', which is not a whitelisted value type.");
        }

        string? sessionIdTypeName = properties
            .Single(property => property.Name == "SessionId")
            .PropertyType
            .Name;
        Assert.AreEqual("String", sessionIdTypeName, "The session id is the only text member a record may carry.");

        Assert.IsFalse(
            properties.Any(property => property.Name.Contains("Message", StringComparison.Ordinal)),
            "A free-text message member must never exist on a log record.");
        Assert.IsFalse(
            properties.Any(property => property.Name.Contains("Detail", StringComparison.Ordinal)),
            "A free-text detail member must never exist on a log record.");
    }

    [TestMethod]
    public void TheForbiddenShapeCheckRefusesPathsMailStyleValuesAndControlCharacters()
    {
        LogRedactor.EnsureNoForbiddenShape(null, "fieldName");
        LogRedactor.EnsureNoForbiddenShape("screenshot", "fieldName");
        LogRedactor.EnsureNoForbiddenShape(FixedSessionId, "fieldName");

        Assert.ThrowsExactly<ArgumentException>(() => LogRedactor.EnsureNoForbiddenShape(@"C:\Users\example\owner.json", "fieldName"));
        Assert.ThrowsExactly<ArgumentException>(() => LogRedactor.EnsureNoForbiddenShape("/path/to/project", "fieldName"));
        Assert.ThrowsExactly<ArgumentException>(() => LogRedactor.EnsureNoForbiddenShape("REDACTED@example.com", "fieldName"));
        Assert.ThrowsExactly<ArgumentException>(() => LogRedactor.EnsureNoForbiddenShape("line one\nline two", "fieldName"));
        Assert.ThrowsExactly<ArgumentException>(() => LogRedactor.EnsureNoForbiddenShape("tab\there", "fieldName"));
        Assert.ThrowsExactly<ArgumentException>(() => LogRedactor.EnsureNoForbiddenShape("bell\u0007", "fieldName"));
    }

    [TestMethod]
    public void DescribeRendersOnlyWhitelistedFieldsAndNoPathOrMailStyleValue()
    {
        LogRecord record = CreateCompleteRecord();

        string description = LogRedactor.Describe(record);

        Assert.AreEqual(
            "2026-10-26T12:34:56.0000000+00:00 information session.ready"
                + " sessionId=" + FixedSessionId
                + " state=ready code=E_NOT_READY method=screenshot durationMs=42 pid=1234 width=1920 height=1080",
            description);
        Assert.IsFalse(description.Contains('\\', StringComparison.Ordinal), "A described record must not contain a path separator.");
        Assert.IsFalse(description.Contains('/', StringComparison.Ordinal), "A described record must not contain a path separator.");
        Assert.IsFalse(description.Contains('@', StringComparison.Ordinal), "A described record must not contain a mail-style value.");
        Assert.IsFalse(description.Contains('\n', StringComparison.Ordinal), "A described record is one line.");

        foreach (string key in new[] { "message", "detail", "path", "title", "user", "account", "sid", "ip" })
        {
            Assert.IsFalse(
                description.Contains(key + "=", StringComparison.OrdinalIgnoreCase),
                "Describe must not add a field outside the whitelist.");
        }
    }

    [TestMethod]
    public void DescribeOnAMinimalRecordNamesOnlyThePresentFields()
    {
        LogRecord record = new(
            FixedTimestamp,
            LogLevel.Information,
            LogEvent.LockAcquired,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);

        Assert.AreEqual("2026-10-26T12:34:56.0000000+00:00 information lock.acquired", LogRedactor.Describe(record));
    }

    [TestMethod]
    public void TheShapeCheckNamesTheFieldWithoutRepeatingAnyValue()
    {
        ArgumentException error = Assert.ThrowsExactly<ArgumentException>(
            () => LogRedactor.EnsureNoForbiddenShape("anything", string.Empty));

        Assert.Contains("fieldName", error.Message, "The failure must name the field, not the value.");
    }

    private static LogRecord CreateCompleteRecord()
    {
        return new LogRecord(
            FixedTimestamp,
            LogLevel.Information,
            LogEvent.SessionReady,
            FixedSessionId,
            SessionState.Ready,
            SessionErrorCode.NotReady,
            SessionTool.Screenshot,
            durationMs: 42,
            pid: 1234,
            width: 1920,
            height: 1080);
    }

    private static LogRecord CreateRecordWithSessionId(string sessionId)
    {
        return new LogRecord(
            FixedTimestamp,
            LogLevel.Information,
            LogEvent.SessionStarting,
            sessionId,
            null,
            null,
            null,
            null,
            null,
            null,
            null);
    }

    private static LogRecord CreateRecordWithDuration(int durationMs)
    {
        return new LogRecord(
            FixedTimestamp,
            LogLevel.Information,
            LogEvent.SessionReady,
            null,
            null,
            null,
            null,
            durationMs,
            null,
            null,
            null);
    }

    private static LogRecord CreateRecordWithPid(int pid)
    {
        return new LogRecord(
            FixedTimestamp,
            LogLevel.Information,
            LogEvent.SessionReady,
            null,
            null,
            null,
            null,
            null,
            pid,
            null,
            null);
    }

    private static LogRecord CreateRecordWithBounds(int width, int height)
    {
        return new LogRecord(
            FixedTimestamp,
            LogLevel.Information,
            LogEvent.SessionReady,
            null,
            null,
            null,
            null,
            null,
            null,
            width,
            height);
    }
}
