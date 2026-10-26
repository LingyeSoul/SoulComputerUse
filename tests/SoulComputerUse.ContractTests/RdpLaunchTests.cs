using System;
using System.IO;
using SoulComputerUse.Core.Session;
using SoulComputerUse.SessionAgent;
using SoulComputerUse.Windows.Rdp;
using SoulComputerUse.Windows.Rdp.Launch;

namespace SoulComputerUse.ContractTests;
/// <summary>
/// Contract tests for the alternate-shell launch layer: the command line the agent's own parser
/// accepts, the path derived from the application directory, and the two control properties written
/// with a read-back check.
/// </summary>
/// <remarks>
/// Nothing here needs the remote desktop control or a real connection: the layer under test only
/// builds strings and writes properties, and its failures are decided before any control is involved.
/// </remarks>
[TestClass]
public sealed class AgentLaunchCommandLineTests
{
    private const string SessionId = "b3f1c2d4-5678-4abc-9def-0a1b2c3d4e5f";
    private const string Nonce = "0123456789ABCDEF0123456789ABCDEF";

    /// <summary>A value shaped like a secret, used to prove that no message echoes a rejected nonce.</summary>
    private const string SecretLookingValue = "SECRETSECRETSECRETSECRETSECRET12";

    private const string PlainExecutablePath = @"C:\app\SoulComputerUse.SessionAgent.exe";
    private const string ExecutablePathWithSpaces = @"C:\Program Files\SoulComputerUse\SoulComputerUse.SessionAgent.exe";

    [TestMethod]
    public void TheCommandLineMatchesTheAgentParserTokenForToken()
    {
        string commandLine = AgentLaunchCommandLine.Build(
            PlainExecutablePath,
            SessionId,
            Nonce,
            AuthTier.HelloEnrolled,
            GateStrength.Strong);

        Assert.AreEqual(
            @"C:\app\SoulComputerUse.SessionAgent.exe --scu-bootstrap --session b3f1c2d4-5678-4abc-9def-0a1b2c3d4e5f --nonce 0123456789ABCDEF0123456789ABCDEF --auth-tier hello-enrolled --gate-strength strong",
            commandLine,
            "The agent parser accepts exactly this order, so the builder must not reorder or pad anything.");
    }

    [TestMethod]
    public void AnExecutablePathWithSpacesIsQuotedAsAWholeAndTheArgumentsAreNot()
    {
        string commandLine = AgentLaunchCommandLine.Build(
            ExecutablePathWithSpaces,
            SessionId,
            Nonce,
            AuthTier.SystemCredential,
            GateStrength.Login);

        Assert.AreEqual(
            "\"C:\\Program Files\\SoulComputerUse\\SoulComputerUse.SessionAgent.exe\" --scu-bootstrap --session b3f1c2d4-5678-4abc-9def-0a1b2c3d4e5f --nonce 0123456789ABCDEF0123456789ABCDEF --auth-tier system-credential --gate-strength login",
            commandLine,
            "Only the executable may be quoted; quoting a flag or a value would change how the agent parses it.");
        Assert.AreEqual(
            2,
            CountQuotes(commandLine),
            "Exactly the two quotes around the executable path may appear.");
    }

    [TestMethod]
    public void TheAgentParserAcceptsTheGeneratedArgumentsAndReportsTheSameValues()
    {
        string commandLine = AgentLaunchCommandLine.Build(
            PlainExecutablePath,
            SessionId,
            Nonce,
            AuthTier.SystemCredential,
            GateStrength.Login);

        // The alternate shell splits the command line itself, so the executable is the first token and
        // everything after it is what the agent's own parser receives.
        string[] tokens = commandLine.Split(' ');
        string[] agentArguments = tokens[1..];

        AgentOptions options = AgentCommandLine.Parse(agentArguments);

        Assert.AreEqual(SessionId, options.SessionId);
        Assert.AreEqual(Nonce, options.Nonce);
        Assert.AreEqual(AuthTier.SystemCredential, options.AuthTier);
        Assert.AreEqual(GateStrength.Login, options.GateStrength);
    }

    [TestMethod]
    public void ANonHexNonceIsRefusedWithoutEchoingIt()
    {
        ArgumentException error = Assert.ThrowsExactly<ArgumentException>(
            () => AgentLaunchCommandLine.Build(
                PlainExecutablePath,
                SessionId,
                SecretLookingValue,
                AuthTier.HelloEnrolled,
                GateStrength.Strong));

        Assert.IsFalse(
            error.Message.Contains(SecretLookingValue, StringComparison.Ordinal),
            "The nonce must never reach an error message, because the message can reach a log or an issue.");
        StringAssert.Contains(error.Message, "32 hexadecimal characters");
    }

    [TestMethod]
    public void ANonUuidSessionIdIsRefusedWithoutEchoingTheNonce()
    {
        ArgumentException error = Assert.ThrowsExactly<ArgumentException>(
            () => AgentLaunchCommandLine.Build(
                PlainExecutablePath,
                "session",
                Nonce,
                AuthTier.HelloEnrolled,
                GateStrength.Strong));

        Assert.IsFalse(
            error.Message.Contains(Nonce, StringComparison.Ordinal),
            "Even a well-formed nonce must never be echoed by another value's rejection.");
        StringAssert.Contains(error.Message, "D-format UUID");
    }

    [TestMethod]
    public void TheNoneTierIsRefused()
    {
        ArgumentException error = Assert.ThrowsExactly<ArgumentException>(
            () => AgentLaunchCommandLine.Build(
                PlainExecutablePath,
                SessionId,
                Nonce,
                AuthTier.None,
                GateStrength.Strong));

        Assert.IsFalse(error.Message.Contains(Nonce, StringComparison.Ordinal));
        StringAssert.Contains(error.Message, "none");
    }

    [TestMethod]
    public void ATierAndStrengthThatDoNotBelongTogetherAreRefused()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => AgentLaunchCommandLine.Build(
                PlainExecutablePath,
                SessionId,
                Nonce,
                AuthTier.HelloEnrolled,
                GateStrength.Login));

        Assert.ThrowsExactly<ArgumentException>(
            () => AgentLaunchCommandLine.Build(
                PlainExecutablePath,
                SessionId,
                Nonce,
                AuthTier.SystemCredential,
                GateStrength.Strong));
    }

    [TestMethod]
    public void ARelativeExecutablePathIsRefused()
    {
        ArgumentException error = Assert.ThrowsExactly<ArgumentException>(
            () => AgentLaunchCommandLine.Build(
                "SoulComputerUse.SessionAgent.exe",
                SessionId,
                Nonce,
                AuthTier.HelloEnrolled,
                GateStrength.Strong));

        Assert.IsFalse(error.Message.Contains(Nonce, StringComparison.Ordinal));
    }

    [TestMethod]
    public void AnEmptyExecutablePathIsRefused()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => AgentLaunchCommandLine.Build(
                string.Empty,
                SessionId,
                Nonce,
                AuthTier.HelloEnrolled,
                GateStrength.Strong));

        Assert.ThrowsExactly<ArgumentException>(
            () => AgentLaunchCommandLine.Build(
                null!,
                SessionId,
                Nonce,
                AuthTier.HelloEnrolled,
                GateStrength.Strong));
    }

    [TestMethod]
    public void TheLogDescriptionCarriesNoNonceNoPathAndNoFileName()
    {
        string description = AgentLaunchCommandLine.DescribeForLog(
            SessionId,
            AuthTier.SystemCredential,
            GateStrength.Login);

        Assert.AreEqual(
            $"session={SessionId} tier=system-credential strength=login",
            description,
            "The description is the fixed field list section 10 allows and nothing else.");
        Assert.IsFalse(description.Contains(Nonce, StringComparison.Ordinal), "The nonce must never reach a log line.");
        Assert.IsFalse(description.Contains(PlainExecutablePath, StringComparison.Ordinal), "An absolute path must never reach a log line.");
        Assert.IsFalse(
            description.Contains(AgentExecutablePath.FileName, StringComparison.Ordinal),
            "Even a bare file name must never reach a log line.");
    }

    [TestMethod]
    public void TheRequestItselfRendersWithoutTheNonceOrAPath()
    {
        AgentLaunchRequest request = new(
            PlainExecutablePath,
            @"C:\app",
            SessionId,
            Nonce,
            AuthTier.HelloEnrolled,
            GateStrength.Strong);

        string rendered = request.ToString();

        Assert.IsFalse(
            rendered.Contains(Nonce, StringComparison.Ordinal),
            "Interpolating a request into a log line must not print the binding nonce.");
        Assert.IsFalse(
            rendered.Contains(PlainExecutablePath, StringComparison.Ordinal),
            "Interpolating a request into a log line must not print an absolute path.");
        Assert.IsFalse(
            rendered.Contains(AgentExecutablePath.FileName, StringComparison.Ordinal),
            "Interpolating a request into a log line must not print the executable file name.");
    }

    private static int CountQuotes(string text)
    {
        int quotes = 0;
        foreach (char character in text)
        {
            if (character == '"')
            {
                quotes++;
            }
        }

        return quotes;
    }
}

/// <summary>
/// The agent executable path must be derived from this program's own directory, so the alternate shell
/// can never be pointed at a program that merely shares the file name.
/// </summary>
[TestClass]
public sealed class AgentExecutablePathTests
{
    [TestMethod]
    public void AnExistingAgentIsResolvedToAnAbsolutePathInsideTheApplicationDirectory()
    {
        string applicationDirectory = NewTemporaryDirectory();
        try
        {
            string executablePath = Path.Combine(applicationDirectory, AgentExecutablePath.FileName);
            File.WriteAllText(executablePath, string.Empty);

            string resolved = AgentExecutablePath.Resolve(applicationDirectory);

            Assert.AreEqual(Path.GetFullPath(executablePath), resolved, "The agent must resolve beside the program.");
            Assert.IsTrue(
                Path.IsPathFullyQualified(resolved),
                "The alternate shell needs a complete path; a bare name would be resolved through PATH inside the session.");
        }
        finally
        {
            Directory.Delete(applicationDirectory, recursive: true);
        }
    }

    [TestMethod]
    public void AMissingAgentIsReportedByNameWithoutTheDirectory()
    {
        string applicationDirectory = NewTemporaryDirectory();
        try
        {
            FileNotFoundException error = Assert.ThrowsExactly<FileNotFoundException>(
                () => AgentExecutablePath.Resolve(applicationDirectory));

            StringAssert.Contains(
                error.Message,
                AgentExecutablePath.FileName,
                "The error must name the executable it looked for, so the operator can see what is missing.");
            Assert.IsFalse(
                error.Message.Contains(applicationDirectory, StringComparison.OrdinalIgnoreCase),
                "The message must never carry the real installation path, because it can reach a console, a screenshot or an issue.");
        }
        finally
        {
            Directory.Delete(applicationDirectory, recursive: true);
        }
    }

    [TestMethod]
    public void AnEmptyApplicationDirectoryIsRefused()
    {
        Assert.ThrowsExactly<ArgumentException>(() => AgentExecutablePath.Resolve(string.Empty));
    }

    [TestMethod]
    public void ALaunchRequestIsDerivedFromTheApplicationDirectoryAlone()
    {
        string applicationDirectory = NewTemporaryDirectory();
        try
        {
            File.WriteAllText(Path.Combine(applicationDirectory, AgentExecutablePath.FileName), string.Empty);

            AgentLaunchRequest request = AgentLaunchRequest.ForApplicationDirectory(
                applicationDirectory,
                "b3f1c2d4-5678-4abc-9def-0a1b2c3d4e5f",
                "0123456789ABCDEF0123456789ABCDEF",
                AuthTier.HelloEnrolled,
                GateStrength.Strong);

            string expectedDirectory = Path.GetFullPath(applicationDirectory);
            Assert.AreEqual(
                Path.GetFullPath(Path.Combine(applicationDirectory, AgentExecutablePath.FileName)),
                request.ExecutablePath,
                "The executable has to be the agent beside the program, never a caller-given program.");
            Assert.AreEqual(expectedDirectory, request.WorkingDirectory, "The agent starts in the application directory.");
        }
        finally
        {
            Directory.Delete(applicationDirectory, recursive: true);
        }
    }

    private static string NewTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "scu-launch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}

/// <summary>
/// The configurator writes both alternate-shell properties and refuses a control that drops a value.
/// A dropped value would leave the child session running the usual shell, where the agent is never
/// started at all.
/// </summary>
[TestClass]
public sealed class RdpLaunchConfiguratorTests
{
    private const string SessionId = "b3f1c2d4-5678-4abc-9def-0a1b2c3d4e5f";
    private const string Nonce = "0123456789ABCDEF0123456789ABCDEF";

    private const string ExecutablePath = @"C:\Program Files\SoulComputerUse\SoulComputerUse.SessionAgent.exe";
    private const string WorkingDirectory = @"C:\Program Files\SoulComputerUse";

    [TestMethod]
    public void ApplyWritesTheCommandLineAndTheWorkingDirectory()
    {
        RecordingLaunchSurface surface = new();
        AgentLaunchRequest request = NewRequest();

        RdpLaunchConfigurator.Apply(surface, request);

        Assert.AreEqual(
            "\"C:\\Program Files\\SoulComputerUse\\SoulComputerUse.SessionAgent.exe\" --scu-bootstrap --session b3f1c2d4-5678-4abc-9def-0a1b2c3d4e5f --nonce 0123456789ABCDEF0123456789ABCDEF --auth-tier system-credential --gate-strength login",
            surface.StartProgram,
            "The session must be told to run the agent, not the usual shell.");
        Assert.AreEqual(WorkingDirectory, surface.WorkingDirectory, "The agent starts in the application directory.");
    }

    [TestMethod]
    public void ApplyRefusesAStartProgramThatDidNotStick()
    {
        RdpSurfaceException error = Assert.ThrowsExactly<RdpSurfaceException>(
            () => RdpLaunchConfigurator.Apply(new DroppingLaunchSurface(), NewRequest()));

        StringAssert.Contains(error.Message, RdpLaunchConfigurator.StartProgramProperty);
        StringAssert.Contains(error.Message, "did not stick");
        Assert.IsFalse(
            error.Message.Contains(Nonce, StringComparison.Ordinal),
            "The command line carries the nonce, so the failure must describe the property only.");
    }

    [TestMethod]
    public void ApplyRefusesAWorkingDirectoryThatDidNotStick()
    {
        RdpSurfaceException error = Assert.ThrowsExactly<RdpSurfaceException>(
            () => RdpLaunchConfigurator.Apply(
                new DroppingLaunchSurface(RdpLaunchConfigurator.WorkingDirectoryProperty),
                NewRequest()));

        StringAssert.Contains(error.Message, RdpLaunchConfigurator.WorkingDirectoryProperty);
        StringAssert.Contains(error.Message, "did not stick");
    }

    private static AgentLaunchRequest NewRequest()
    {
        return new AgentLaunchRequest(
            ExecutablePath,
            WorkingDirectory,
            SessionId,
            Nonce,
            AuthTier.SystemCredential,
            GateStrength.Login);
    }

    /// <summary>A surface that keeps what it is given, standing in for the control's properties.</summary>
    private sealed class RecordingLaunchSurface : IRdpLaunchSurface
    {
        public string StartProgram { get; set; } = string.Empty;

        public string WorkingDirectory { get; set; } = string.Empty;
    }

    /// <summary>
    /// A surface that silently changes one written value, the way a control that does not support a
    /// property would: the caller's write is accepted and something else comes back.
    /// </summary>
    private sealed class DroppingLaunchSurface : IRdpLaunchSurface
    {
        private readonly string _droppedProperty;
        private string _startProgram = string.Empty;
        private string _workingDirectory = string.Empty;

        internal DroppingLaunchSurface()
            : this(RdpLaunchConfigurator.StartProgramProperty)
        {
        }

        internal DroppingLaunchSurface(string droppedProperty)
        {
            _droppedProperty = droppedProperty;
        }

        public string StartProgram
        {
            get => _startProgram;
            set => _startProgram = Drop(RdpLaunchConfigurator.StartProgramProperty, value);
        }

        public string WorkingDirectory
        {
            get => _workingDirectory;
            set => _workingDirectory = Drop(RdpLaunchConfigurator.WorkingDirectoryProperty, value);
        }

        private string Drop(string propertyName, string value)
        {
            return string.Equals(propertyName, _droppedProperty, StringComparison.Ordinal)
                ? value + " --dropped"
                : value;
        }
    }
}
