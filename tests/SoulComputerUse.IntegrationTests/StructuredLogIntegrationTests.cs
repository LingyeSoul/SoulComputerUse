using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Logging;

namespace SoulComputerUse.IntegrationTests;

/// <summary>
/// The structured log layer against a real <see cref="StringWriter"/>: JSON Lines output, the key set of
/// each line, concurrent writes and the propagation of a destination failure. No real log file is
/// touched, so the suite cannot leave machine state behind.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class StructuredLogIntegrationTests
{
    private const string FixedSessionId = "1f2e3d4c-5b6a-4798-8a9b-0c1d2e3f4a5b";
    private const int ConcurrentWriteCount = 50;

    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 10, 26, 12, 34, 56, TimeSpan.Zero);

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

    [TestMethod]
    public void ThreeRecordsAreWrittenAsThreeParseableJsonLines()
    {
        using StringWriter output = new();
        StructuredLogWriter writer = new(output);

        writer.Write(CreateRecord(LogEvent.LockAcquired, LogLevel.Information, SessionState.Starting));
        writer.Write(CreateRecord(LogEvent.SessionReady, LogLevel.Information, SessionState.Ready));
        writer.Write(CreateRecord(LogEvent.SessionDisconnected, LogLevel.Error, SessionState.Disconnected));

        string[] lines = SplitLines(output.ToString());

        Assert.HasCount(3, lines, "One record is one line.");

        HashSet<string> allowedKeys = new(WhitelistedWireNames, StringComparer.Ordinal);
        foreach (string line in lines)
        {
            using JsonDocument document = JsonDocument.Parse(line);
            Assert.AreEqual(JsonValueKind.Object, document.RootElement.ValueKind);

            List<string> keys = document.RootElement.EnumerateObject().Select(property => property.Name).ToList();
            Assert.IsNotEmpty(keys, "A line must carry at least the timestamp, the level and the event.");
            foreach (string key in keys)
            {
                Assert.Contains(key, allowedKeys, $"The line carries '{key}', which is outside the contract whitelist.");
            }
        }

        Assert.AreEqual("lock.acquired", ReadEvent(lines[0]));
        Assert.AreEqual("session.ready", ReadEvent(lines[1]));
        Assert.AreEqual("session.disconnected", ReadEvent(lines[2]));
    }

    [TestMethod]
    public void ConcurrentWritesProduceWholeParseableLines()
    {
        using StringWriter output = new();
        StructuredLogWriter writer = new(output);

        Parallel.For(0, ConcurrentWriteCount, index =>
        {
            writer.Write(CreateRecord(LogEvent.AgentSpawned, LogLevel.Information, SessionState.Connecting));
        });

        string[] lines = SplitLines(output.ToString());

        Assert.HasCount(ConcurrentWriteCount, lines, "Every concurrent write must produce exactly one line.");

        foreach (string line in lines)
        {
            using JsonDocument document = JsonDocument.Parse(line);

            Assert.AreEqual(FixedTimestamp, ReadTimestamp(document.RootElement), "A torn line would not parse back to the input.");
            Assert.AreEqual("agent.spawned", document.RootElement.GetProperty("event").GetString());
            Assert.AreEqual(FixedSessionId, document.RootElement.GetProperty("sessionId").GetString());
            Assert.AreEqual("connecting", document.RootElement.GetProperty("state").GetString());
            Assert.AreEqual(7, document.RootElement.GetProperty("durationMs").GetInt32());
            Assert.AreEqual(1920, document.RootElement.GetProperty("width").GetInt32());
            Assert.AreEqual(1080, document.RootElement.GetProperty("height").GetInt32());
        }
    }

    [TestMethod]
    public void TheNullWriterDiscardsRecordsAndNeverWritesToTheConsole()
    {
        TextWriter originalOutput = Console.Out;
        using StringWriter console = new();
        Console.SetOut(console);
        try
        {
            StructuredLogWriter writer = StructuredLogWriter.Null;

            Assert.IsTrue(writer.IsNull, "The Null instance must be recognizable.");
            writer.Write(CreateRecord(LogEvent.SessionClosing, LogLevel.Information, SessionState.Closing));

            Assert.IsTrue(ReferenceEquals(writer, StructuredLogWriter.Null), "Null is a shared stateless instance, not a console writer.");
            Assert.IsEmpty(console.ToString(), "An unconfigured log must stay silent instead of falling back to the console.");
        }
        finally
        {
            Console.SetOut(originalOutput);
        }
    }

    [TestMethod]
    public void ARejectingDestinationPropagatesTheFailure()
    {
        using ThrowingWriter destination = new();
        StructuredLogWriter writer = new(destination);

        Assert.ThrowsExactly<IOException>(
            () => writer.Write(CreateRecord(LogEvent.TeardownFailed, LogLevel.Error, SessionState.Closing)));

        Assert.IsGreaterThan(0, destination.Attempts, "The writer must really try to write before the failure surfaces.");
    }

    [TestMethod]
    public void ADisposedDestinationIsReportedInsteadOfSwallowed()
    {
        StringWriter output = new();
        output.Dispose();
        StructuredLogWriter writer = new(output);

        Assert.ThrowsExactly<ObjectDisposedException>(
            () => writer.Write(CreateRecord(LogEvent.LockReleased, LogLevel.Information, SessionState.Closing)));
    }

    private static LogRecord CreateRecord(LogEvent logEvent, LogLevel level, SessionState state)
    {
        return new LogRecord(
            FixedTimestamp,
            level,
            logEvent,
            FixedSessionId,
            state,
            SessionErrorCode.NotReady,
            SessionTool.Screenshot,
            durationMs: 7,
            pid: Environment.ProcessId,
            width: 1920,
            height: 1080);
    }

    private static string[] SplitLines(string text)
    {
        return text.Split(
            ["\r\n", "\n"],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string ReadEvent(string line)
    {
        using JsonDocument document = JsonDocument.Parse(line);
        return document.RootElement.GetProperty("event").GetString()!;
    }

    private static DateTimeOffset ReadTimestamp(JsonElement root)
    {
        return DateTimeOffset.Parse(root.GetProperty("ts").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>A destination whose every write fails, standing in for a full or closed log file.</summary>
    private sealed class ThrowingWriter : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        internal int Attempts { get; private set; }

        public override void Write(char value)
        {
            Attempts++;
            throw new IOException("The destination rejected the write.");
        }

        public override void Write(string? value)
        {
            Attempts++;
            throw new IOException("The destination rejected the write.");
        }

        public override void WriteLine(string? value)
        {
            Attempts++;
            throw new IOException("The destination rejected the write.");
        }
    }
}
