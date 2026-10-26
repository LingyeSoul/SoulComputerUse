using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Cli;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;

namespace SoulComputerUse.ContractTests;

/// <summary>
/// Contract tests for the CLI adapter in section 6 of the interface contract: command parsing, the
/// structured output lines, and the local prompt. Every test drives string readers and writers, so
/// no terminal, session, or machine state is used.
/// </summary>
[TestClass]
public sealed class CliCommandTests
{
    private const string SessionId = "b3f1c2d4-5678-4abc-9def-0a1b2c3d4e5f";

    [TestMethod]
    public void StatusShotMoveScrollAndQuitParseTheirContractParameters()
    {
        Assert.AreEqual(CliCommandKind.Status, CliCommandParser.Parse(["status"]).Kind);
        Assert.AreEqual(CliCommandKind.Quit, CliCommandParser.Parse(["quit"]).Kind);
        Assert.AreEqual(CliCommandKind.Help, CliCommandParser.Parse(["--help"]).Kind);

        CliCommand shot = CliCommandParser.Parse(["shot", "--out", "shot.png"]);
        Assert.AreEqual(CliCommandKind.Shot, shot.Kind);
        Assert.AreEqual("shot.png", shot.OutputPath);
        Assert.IsNull(CliCommandParser.Parse(["shot"]).OutputPath);

        CliCommand move = CliCommandParser.Parse(["move", "12", "34"]);
        Assert.AreEqual(CliCommandKind.Move, move.Kind);
        Assert.AreEqual(12, move.X);
        Assert.AreEqual(34, move.Y);

        CliCommand scroll = CliCommandParser.Parse(["scroll", "10", "20", "-3", "4"]);
        Assert.AreEqual(CliCommandKind.Scroll, scroll.Kind);
        Assert.AreEqual(10, scroll.X);
        Assert.AreEqual(20, scroll.Y);
        Assert.AreEqual(-3, scroll.DeltaX);
        Assert.AreEqual(4, scroll.DeltaY);
    }

    [TestMethod]
    public void ClickRequiresItsCoordinatesButtonAndCount()
    {
        CliCommand click = CliCommandParser.Parse(["click", "5", "6", "--button", "right", "--count", "2"]);
        Assert.AreEqual(CliCommandKind.Click, click.Kind);
        Assert.AreEqual(5, click.X);
        Assert.AreEqual(6, click.Y);
        Assert.AreEqual(MouseButton.Right, click.Button);
        Assert.AreEqual(2, click.Count);

        CliCommand reordered = CliCommandParser.Parse(["click", "5", "6", "--count", "3", "--button", "middle"]);
        Assert.AreEqual(MouseButton.Middle, reordered.Button);
        Assert.AreEqual(3, reordered.Count);

        AssertParseCode(SessionErrorCode.InputInvalid, ["click", "5", "6", "--button", "left"]);
        AssertParseCode(SessionErrorCode.InputInvalid, ["click", "5", "6", "--count", "1"]);
        AssertParseCode(SessionErrorCode.InputInvalid, ["click", "5", "6", "--button", "left", "--count", "1", "--bogus", "2"]);
        AssertParseCode(SessionErrorCode.InputInvalid, ["click", "5", "6", "--button", "side", "--count", "1"]);
        AssertParseCode(SessionErrorCode.InputInvalid, ["click", "5", "6", "--button", "left", "--count", "0"]);
        AssertParseCode(SessionErrorCode.InputInvalid, ["click", "5", "6", "--button", "left", "--count", "4"]);
        AssertParseCode(SessionErrorCode.InputInvalid, ["click", "5", "6", "--button", "left", "--count", "two"]);
        AssertParseCode(SessionErrorCode.InputInvalid, ["click", "5", "6", "--button", "left", "--count"]);
    }

    [TestMethod]
    public void TypeKeepsSpacesAndUsesTheContractLimit()
    {
        Assert.AreEqual("hello world", CliCommandParser.Parse(["type", "hello", "world"]).Text);
        Assert.AreEqual("hello  world", CliCommandParser.Parse(["type", "hello  world"]).Text);
        Assert.AreEqual("hello  world", CliCommandParser.ParseLine("type hello  world").Text);

        string maximum = new('a', SessionToolArguments.MaximumTextLength);
        Assert.AreEqual(maximum, CliCommandParser.Parse(["type", maximum]).Text);

        AssertParseCode(SessionErrorCode.InputInvalid, ["type"]);
        AssertParseCode(SessionErrorCode.InputInvalid, ["type", new string('a', SessionToolArguments.MaximumTextLength + 1)]);
        AssertParseLineCode(SessionErrorCode.InputInvalid, "type   ");
    }

    [TestMethod]
    public void KeyCombinationsUseOnlyTheContractKeys()
    {
        CliCommand keys = CliCommandParser.Parse(["key", "ctrl+alt+delete"]);
        Assert.AreEqual(CliCommandKind.Key, keys.Kind);
        Assert.HasCount(3, keys.Keys);
        Assert.AreEqual(SessionKey.Ctrl, keys.Keys[0]);
        Assert.AreEqual(SessionKey.Alt, keys.Keys[1]);
        Assert.AreEqual(SessionKey.Delete, keys.Keys[2]);

        CliCommand five = CliCommandParser.Parse(["key", "ctrl+alt+shift+win+enter"]);
        Assert.HasCount(5, five.Keys);

        AssertParseCode(SessionErrorCode.InputInvalid, ["key"]);
        AssertParseCode(SessionErrorCode.InputInvalid, ["key", "ctrl+hyper"]);
        AssertParseCode(SessionErrorCode.InputInvalid, ["key", "ctrl+"]);
        AssertParseCode(SessionErrorCode.InputInvalid, ["key", "ctrl+alt+shift+win+enter+esc"]);
        AssertParseCode(SessionErrorCode.InputInvalid, ["key", "ctrl", "alt"]);
    }

    [TestMethod]
    public void CoordinatesRejectNonNumbersAndNegativeValuesWithTheContractCodes()
    {
        AssertParseCode(SessionErrorCode.InputInvalid, ["move", "left", "0"]);
        AssertParseCode(SessionErrorCode.InputInvalid, ["move", "0"]);
        AssertParseCode(SessionErrorCode.CoordOutOfRange, ["move", "-1", "0"]);
        AssertParseCode(SessionErrorCode.CoordOutOfRange, ["move", "0", "-1"]);
        AssertParseCode(SessionErrorCode.InputInvalid, ["scroll", "1", "2", "a", "4"]);
        AssertParseCode(SessionErrorCode.InputInvalid, ["scroll", "1", "2", "3"]);
        AssertParseCode(SessionErrorCode.CoordOutOfRange, ["scroll", "-1", "2", "3", "4"]);
    }

    [TestMethod]
    public void UnknownCommandsOptionsAndExtraArgumentsAreRefused()
    {
        AssertParseCode(SessionErrorCode.InputInvalid, []);
        AssertParseCode(SessionErrorCode.InputInvalid, ["shell"]);
        AssertParseCode(SessionErrorCode.InputInvalid, ["status", "extra"]);
        AssertParseCode(SessionErrorCode.InputInvalid, ["quit", "extra"]);
        AssertParseCode(SessionErrorCode.InputInvalid, ["--help", "extra"]);
        AssertParseCode(SessionErrorCode.InputInvalid, ["shot", "--format", "png"]);
        AssertParseCode(SessionErrorCode.InputInvalid, ["shot", "--out"]);
        AssertParseCode(SessionErrorCode.InputInvalid, ["shot", "--out", " "]);
    }

    [TestMethod]
    public void ParseLineSplitsTokensAndKeepsTypeText()
    {
        Assert.AreEqual(CliCommandKind.Status, CliCommandParser.ParseLine("  status  ").Kind);
        Assert.AreEqual(CliCommandKind.Help, CliCommandParser.ParseLine("--help").Kind);
        Assert.AreEqual(CliCommandKind.Click, CliCommandParser.ParseLine("click 1 2 --button left --count 1").Kind);
        Assert.AreEqual("typed into the session", CliCommandParser.ParseLine("type typed into the session").Text);

        AssertParseLineCode(SessionErrorCode.InputInvalid, "   ");
        AssertParseLineCode(SessionErrorCode.InputInvalid, "nope");
    }

    [TestMethod]
    public async Task StatusPrintsTheContractLineWithAndWithoutTheHelloHint()
    {
        TestHarness withHint = TestHarness.Create(CreateStatus("helloNotEnrolled"), string.Empty);
        int withHintExit = await withHint.Runner.RunSingleAsync(["status"], CancellationToken.None);

        Assert.AreEqual(0, withHintExit);
        Assert.AreEqual(
            $"state=ready sessionId={SessionId} authTier=system-credential gateStrength=login screen=1920x1080 helloHint=helloNotEnrolled\n",
            withHint.Output.ToString());
        Assert.AreEqual(string.Empty, withHint.Error.ToString());
        Assert.AreEqual(1, withHint.Client.StatusCalls);

        TestHarness withoutHint = TestHarness.Create(CreateStatus(null), string.Empty);
        int withoutHintExit = await withoutHint.Runner.RunSingleAsync(["status"], CancellationToken.None);

        Assert.AreEqual(0, withoutHintExit);
        Assert.AreEqual(
            $"state=ready sessionId={SessionId} authTier=system-credential gateStrength=login screen=1920x1080\n",
            withoutHint.Output.ToString());
    }

    [TestMethod]
    public async Task MoveChecksTheScreenAndReportsOk()
    {
        TestHarness harness = TestHarness.Create(CreateStatus(null), string.Empty);

        int exitCode = await harness.Runner.RunSingleAsync(["move", "10", "20"], CancellationToken.None);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual("ok\n", harness.Output.ToString());
        Assert.AreEqual(string.Empty, harness.Error.ToString());
        Assert.AreEqual(1, harness.Client.StatusCalls);
        Assert.AreEqual(1, harness.Client.MouseMoveCalls);
        Assert.AreEqual(10, harness.Client.LastMoveX);
        Assert.AreEqual(20, harness.Client.LastMoveY);
    }

    [TestMethod]
    public async Task CoordinateBeyondTheScreenFailsWithTheContractCode()
    {
        TestHarness harness = TestHarness.Create(CreateStatus(null), string.Empty);

        int exitCode = await harness.Runner.RunSingleAsync(["move", "1920", "0"], CancellationToken.None);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(string.Empty, harness.Output.ToString());

        string errorText = harness.Error.ToString();
        StringAssert.StartsWith(errorText, "E_COORD_OUT_OF_RANGE ");
        StringAssert.Contains(errorText, "0 <= x < 1920");
        Assert.HasCount(1, errorText.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.AreEqual(0, harness.Client.MouseMoveCalls);
    }

    [TestMethod]
    public async Task ClickScrollAndKeyForwardTheirContractArguments()
    {
        TestHarness click = TestHarness.Create(CreateStatus(null), string.Empty);
        int clickExit = await click.Runner.RunSingleAsync(
            ["click", "5", "6", "--button", "right", "--count", "2"],
            CancellationToken.None);

        Assert.AreEqual(0, clickExit);
        Assert.AreEqual("ok\n", click.Output.ToString());
        Assert.AreEqual(5, click.Client.LastClickX);
        Assert.AreEqual(6, click.Client.LastClickY);
        Assert.AreEqual(MouseButton.Right, click.Client.LastClickButton);
        Assert.AreEqual(2, click.Client.LastClickCount);

        TestHarness scroll = TestHarness.Create(CreateStatus(null), string.Empty);
        int scrollExit = await scroll.Runner.RunSingleAsync(["scroll", "10", "20", "-3", "4"], CancellationToken.None);

        Assert.AreEqual(0, scrollExit);
        Assert.AreEqual("ok\n", scroll.Output.ToString());
        Assert.AreEqual(10, scroll.Client.LastScrollX);
        Assert.AreEqual(20, scroll.Client.LastScrollY);
        Assert.AreEqual(-3, scroll.Client.LastScrollDeltaX);
        Assert.AreEqual(4, scroll.Client.LastScrollDeltaY);

        TestHarness key = TestHarness.Create(CreateStatus(null), string.Empty);
        int keyExit = await key.Runner.RunSingleAsync(["key", "ctrl+alt+delete"], CancellationToken.None);

        Assert.AreEqual(0, keyExit);
        Assert.AreEqual("ok\n", key.Output.ToString());
        Assert.HasCount(3, key.Client.LastKeys);
        Assert.AreEqual(SessionKey.Ctrl, key.Client.LastKeys[0]);
        Assert.AreEqual(SessionKey.Alt, key.Client.LastKeys[1]);
        Assert.AreEqual(SessionKey.Delete, key.Client.LastKeys[2]);
    }

    [TestMethod]
    public async Task TypeSendsTheTextButNeverEchoesIt()
    {
        TestHarness harness = TestHarness.Create(CreateStatus(null), string.Empty);
        const string TypedText = "hunter2 correct horse";

        int exitCode = await harness.Runner.RunSingleAsync(
            ["type", "hunter2", "correct", "horse"],
            CancellationToken.None);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual("ok\n", harness.Output.ToString());
        Assert.AreEqual(string.Empty, harness.Error.ToString());
        Assert.AreEqual(TypedText, harness.Client.LastTypedText);
        Assert.IsFalse(harness.Output.ToString().Contains("hunter2", StringComparison.Ordinal));
        Assert.IsFalse(harness.Error.ToString().Contains("hunter2", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ScreenshotWritesTheReturnedPngToTheRequestedPath()
    {
        string directory = Path.Combine(Path.GetTempPath(), "scu-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            string path = Path.Combine(directory, "shot.png");
            byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01];
            TestHarness harness = TestHarness.Create(CreateStatus(null), string.Empty);
            harness.Client.ScreenshotBytes = png;

            int exitCode = await harness.Runner.RunSingleAsync(["shot", "--out", path], CancellationToken.None);

            Assert.AreEqual(0, exitCode);
            Assert.AreEqual($"ok {path}\n", harness.Output.ToString());
            Assert.AreEqual(string.Empty, harness.Error.ToString());
            Assert.AreEqual(1, harness.Client.ScreenshotCalls);
            Assert.AreEqual(0, harness.Client.StatusCalls);
            Assert.IsTrue(File.Exists(path));
            CollectionAssert.AreEqual(png, File.ReadAllBytes(path));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task ScreenshotWithoutOutPrintsOneBase64Line()
    {
        TestHarness harness = TestHarness.Create(CreateStatus(null), string.Empty);
        harness.Client.ScreenshotBytes = [0x01, 0x02, 0x03];

        int exitCode = await harness.Runner.RunSingleAsync(["shot"], CancellationToken.None);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual("png=AQID\n", harness.Output.ToString());
        Assert.AreEqual(string.Empty, harness.Error.ToString());
    }

    [TestMethod]
    public async Task ASessionRefusalPrintsItsWireCodeAndExitsNonZero()
    {
        TestHarness harness = TestHarness.Create(CreateStatus(null), string.Empty);
        harness.Client.Failure = new SessionOperationException(
            SessionErrorCode.AwaitingCredentials,
            "The session waits for the operator to finish the system verification step themselves.",
            false);

        int exitCode = await harness.Runner.RunSingleAsync(["key", "enter"], CancellationToken.None);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(string.Empty, harness.Output.ToString());
        StringAssert.StartsWith(harness.Error.ToString(), "E_AWAITING_CREDENTIALS ");
    }

    [TestMethod]
    public async Task AScreenshotWriteFailureNamesNoPath()
    {
        string missingDirectory = Path.Combine(
            Path.GetTempPath(),
            "scu-cli-missing-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(missingDirectory, "shot.png");
        TestHarness harness = TestHarness.Create(CreateStatus(null), string.Empty);

        int exitCode = await harness.Runner.RunSingleAsync(["shot", "--out", path], CancellationToken.None);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(string.Empty, harness.Output.ToString());

        string errorText = harness.Error.ToString();
        StringAssert.StartsWith(errorText, "E_INPUT_INVALID ");
        Assert.IsFalse(errorText.Contains(missingDirectory, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(Directory.Exists(missingDirectory));
    }

    [TestMethod]
    public async Task QuitInSingleCommandModeSucceedsWithoutTouchingTheSession()
    {
        TestHarness harness = TestHarness.Create(CreateStatus(null), string.Empty);

        int exitCode = await harness.Runner.RunSingleAsync(["quit"], CancellationToken.None);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual("ok\n", harness.Output.ToString());
        Assert.AreEqual(string.Empty, harness.Error.ToString());
        Assert.AreEqual(0, harness.Client.Calls);
    }

    [TestMethod]
    public async Task HelpPrintsTheCommandTableAndSucceeds()
    {
        TestHarness harness = TestHarness.Create(CreateStatus(null), string.Empty);

        int exitCode = await harness.Runner.RunSingleAsync(["--help"], CancellationToken.None);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(string.Empty, harness.Error.ToString());
        Assert.AreEqual(0, harness.Client.Calls);

        string output = harness.Output.ToString();
        string[] expectedInvocations =
        [
            "status",
            "shot [--out <path>]",
            "move <x> <y>",
            "click <x> <y> --button <left|right|middle> --count <n>",
            "scroll <x> <y> <dx> <dy>",
            "type <text>",
            "key <k1>+<k2>...",
            "quit",
            "--help",
        ];

        foreach (string invocation in expectedInvocations)
        {
            StringAssert.Contains(output, invocation);
        }
    }

    [TestMethod]
    public async Task NoArgumentsStartsThePromptAndEndOfInputExits()
    {
        TestHarness harness = TestHarness.Create(CreateStatus(null), string.Empty);

        int exitCode = await harness.Runner.RunReplAsync(CancellationToken.None);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual("scu> \n", harness.Output.ToString());
        Assert.AreEqual(string.Empty, harness.Error.ToString());
        Assert.AreEqual(0, harness.Client.Calls);
    }

    [TestMethod]
    public async Task ThePromptRunsCommandsUntilQuit()
    {
        TestHarness harness = TestHarness.Create(CreateStatus(null), "status\nquit\n");

        int exitCode = await harness.Runner.RunReplAsync(CancellationToken.None);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(
            $"scu> state=ready sessionId={SessionId} authTier=system-credential gateStrength=login screen=1920x1080\nscu> ok\n",
            harness.Output.ToString());
        Assert.AreEqual(string.Empty, harness.Error.ToString());
        Assert.AreEqual(1, harness.Client.StatusCalls);
    }

    [TestMethod]
    public async Task ThePromptReportsAParseErrorAndKeepsRunning()
    {
        TestHarness harness = TestHarness.Create(CreateStatus(null), "nope\nquit\n");

        int exitCode = await harness.Runner.RunReplAsync(CancellationToken.None);

        Assert.AreEqual(0, exitCode);
        StringAssert.StartsWith(harness.Error.ToString(), "E_INPUT_INVALID ");
        StringAssert.EndsWith(harness.Output.ToString(), "scu> ok\n");
    }

    [TestMethod]
    public async Task UnknownCommandExitsNonZeroWithOneStructuredErrorLine()
    {
        TestHarness harness = TestHarness.Create(CreateStatus(null), string.Empty);

        int exitCode = await harness.Runner.RunSingleAsync(["shell"], CancellationToken.None);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(string.Empty, harness.Output.ToString());

        string errorText = harness.Error.ToString();
        StringAssert.StartsWith(errorText, "E_INPUT_INVALID ");
        Assert.HasCount(1, errorText.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    private static SessionStatus CreateStatus(string? helloHint)
    {
        return new SessionStatus(
            "ready",
            SessionId,
            AuthTier.SystemCredential,
            GateStrength.Login,
            new SessionScreen(1920, 1080),
            helloHint);
    }

    private static void AssertParseCode(SessionErrorCode expected, string[] args)
    {
        CliParseException error = Assert.ThrowsExactly<CliParseException>(() => CliCommandParser.Parse(args));
        Assert.AreEqual(expected, error.Code);
    }

    private static void AssertParseLineCode(SessionErrorCode expected, string line)
    {
        CliParseException error = Assert.ThrowsExactly<CliParseException>(() => CliCommandParser.ParseLine(line));
        Assert.AreEqual(expected, error.Code);
    }

    private sealed record TestHarness(
        CliRunner Runner,
        FakeSessionClient Client,
        StringWriter Output,
        StringWriter Error)
    {
        internal static TestHarness Create(SessionStatus status, string input)
        {
            FakeSessionClient client = new(status);
            StringWriter output = new(CultureInfo.InvariantCulture) { NewLine = "\n" };
            StringWriter error = new(CultureInfo.InvariantCulture) { NewLine = "\n" };
            CliRunner runner = new(client, new StringReader(input), output, error);
            return new TestHarness(runner, client, output, error);
        }
    }

    private sealed class FakeSessionClient : ISessionClient
    {
        internal FakeSessionClient(SessionStatus status)
        {
            Status = status;
        }

        internal SessionStatus Status { get; }

        internal byte[] ScreenshotBytes { get; set; } = [0x89, 0x50, 0x4E, 0x47];

        internal int Calls { get; private set; }

        internal int StatusCalls { get; private set; }

        internal int ScreenshotCalls { get; private set; }

        internal int MouseMoveCalls { get; private set; }

        internal int LastMoveX { get; private set; }

        internal int LastMoveY { get; private set; }

        internal int LastClickX { get; private set; }

        internal int LastClickY { get; private set; }

        internal MouseButton LastClickButton { get; private set; } = MouseButton.Left;

        internal int LastClickCount { get; private set; }

        internal int LastScrollX { get; private set; }

        internal int LastScrollY { get; private set; }

        internal int LastScrollDeltaX { get; private set; }

        internal int LastScrollDeltaY { get; private set; }

        internal IReadOnlyList<SessionKey> LastKeys { get; private set; } = [];

        internal string? LastTypedText { get; private set; }

        internal SessionOperationException? Failure { get; set; }

        public Task<SessionStatus> GetStatusAsync(CancellationToken cancellationToken)
        {
            Calls++;
            StatusCalls++;
            ThrowIfFailing();
            return Task.FromResult(Status);
        }

        public Task<byte[]> ScreenshotAsync(CancellationToken cancellationToken)
        {
            Calls++;
            ScreenshotCalls++;
            ThrowIfFailing();
            return Task.FromResult(ScreenshotBytes);
        }

        public Task MouseMoveAsync(int x, int y, CancellationToken cancellationToken)
        {
            Calls++;
            MouseMoveCalls++;
            ThrowIfFailing();
            LastMoveX = x;
            LastMoveY = y;
            return Task.CompletedTask;
        }

        public Task MouseClickAsync(int x, int y, MouseButton button, int count, CancellationToken cancellationToken)
        {
            Calls++;
            ThrowIfFailing();
            LastClickX = x;
            LastClickY = y;
            LastClickButton = button;
            LastClickCount = count;
            return Task.CompletedTask;
        }

        public Task MouseScrollAsync(int x, int y, int deltaX, int deltaY, CancellationToken cancellationToken)
        {
            Calls++;
            ThrowIfFailing();
            LastScrollX = x;
            LastScrollY = y;
            LastScrollDeltaX = deltaX;
            LastScrollDeltaY = deltaY;
            return Task.CompletedTask;
        }

        public Task KeyTypeAsync(string text, CancellationToken cancellationToken)
        {
            Calls++;
            ThrowIfFailing();
            LastTypedText = text;
            return Task.CompletedTask;
        }

        public Task KeyPressAsync(IReadOnlyList<SessionKey> keys, CancellationToken cancellationToken)
        {
            Calls++;
            ThrowIfFailing();
            LastKeys = keys;
            return Task.CompletedTask;
        }

        private void ThrowIfFailing()
        {
            if (Failure is not null)
            {
                throw Failure;
            }
        }
    }
}