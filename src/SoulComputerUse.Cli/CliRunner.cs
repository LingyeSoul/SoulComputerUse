using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;

namespace SoulComputerUse.Cli;

/// <summary>
/// Executes parsed commands against the shared session client and owns the interactive prompt.
/// Output is the contract's structured line form: one status line, <c>ok</c> for a completed
/// action, or <c>&lt;CODE&gt; &lt;message&gt;</c> on the error writer. Typed text is never written
/// to either writer.
/// </summary>
internal sealed class CliRunner
{
    private const int FailureExitCode = 1;
    private const string Prompt = "scu> ";
    private const string ScreenshotWriteFailure =
        "The screenshot could not be written to the requested output. Check that the target directory exists and is writable.";

    private readonly ISessionClient _client;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly TextWriter _error;

    /// <summary>Creates a runner over one session client and one pair of text writers.</summary>
    internal CliRunner(ISessionClient client, TextReader input, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        _client = client;
        _input = input;
        _output = output;
        _error = error;
    }

    /// <summary>Parses and runs one command, then returns its exit code.</summary>
    internal async Task<int> RunSingleAsync(string[] args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);

        CliCommand command;
        try
        {
            command = CliCommandParser.Parse(args);
        }
        catch (CliParseException error)
        {
            return WriteError(error.Code, error.Message);
        }

        return await ExecuteAsync(command, cancellationToken);
    }

    /// <summary>
    /// Runs the interactive prompt until <c>quit</c>, end of input, or cancellation. A failed
    /// command prints one error line and the prompt keeps accepting commands.
    /// </summary>
    internal async Task<int> RunReplAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            _output.Write(Prompt);
            _output.Flush();

            string? line = await _input.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                _output.WriteLine();
                return 0;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            CliCommand command;
            try
            {
                command = CliCommandParser.ParseLine(line);
            }
            catch (CliParseException error)
            {
                WriteError(error.Code, error.Message);
                continue;
            }

            if (command.Kind == CliCommandKind.Quit)
            {
                _output.WriteLine("ok");
                return 0;
            }

            await ExecuteAsync(command, cancellationToken);
        }
    }

    private async Task<int> ExecuteAsync(CliCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        try
        {
            return await ExecuteCoreAsync(command, cancellationToken);
        }
        catch (SessionOperationException error)
        {
            return WriteError(error.Code, error.Message);
        }
    }

    private async Task<int> ExecuteCoreAsync(CliCommand command, CancellationToken cancellationToken)
    {
        switch (command.Kind)
        {
            case CliCommandKind.Status:
                return await ExecuteStatusAsync(cancellationToken);

            case CliCommandKind.Shot:
                return await ExecuteScreenshotAsync(command, cancellationToken);

            case CliCommandKind.Move:
                await RequireCoordinateAsync(command.X, command.Y, cancellationToken);
                await _client.MouseMoveAsync(command.X, command.Y, cancellationToken);
                return WriteOk();

            case CliCommandKind.Click:
                await RequireCoordinateAsync(command.X, command.Y, cancellationToken);
                await _client.MouseClickAsync(command.X, command.Y, command.Button, command.Count, cancellationToken);
                return WriteOk();

            case CliCommandKind.Scroll:
                await RequireCoordinateAsync(command.X, command.Y, cancellationToken);
                await _client.MouseScrollAsync(command.X, command.Y, command.DeltaX, command.DeltaY, cancellationToken);
                return WriteOk();

            case CliCommandKind.Type:
                await _client.KeyTypeAsync(command.Text, cancellationToken);
                return WriteOk();

            case CliCommandKind.Key:
                await _client.KeyPressAsync(command.Keys, cancellationToken);
                return WriteOk();

            case CliCommandKind.Quit:
                return WriteOk();

            case CliCommandKind.Help:
                _output.WriteLine(CliHelp.Text);
                return 0;

            default:
                throw new ArgumentOutOfRangeException(nameof(command), command.Kind, "Unknown CLI command kind.");
        }
    }

    private async Task<int> ExecuteStatusAsync(CancellationToken cancellationToken)
    {
        SessionStatus status = await ReadStatusAsync(cancellationToken);
        _output.WriteLine(FormatStatus(status));
        return 0;
    }

    private async Task<int> ExecuteScreenshotAsync(CliCommand command, CancellationToken cancellationToken)
    {
        byte[] png = await _client.ScreenshotAsync(cancellationToken);
        ArgumentNullException.ThrowIfNull(png);

        if (command.OutputPath is null)
        {
            _output.WriteLine($"png={Convert.ToBase64String(png)}");
            return 0;
        }

        try
        {
            await File.WriteAllBytesAsync(command.OutputPath, png, cancellationToken);
        }
        catch (IOException)
        {
            throw ScreenshotWriteError();
        }
        catch (UnauthorizedAccessException)
        {
            throw ScreenshotWriteError();
        }
        catch (ArgumentException)
        {
            throw ScreenshotWriteError();
        }
        catch (NotSupportedException)
        {
            throw ScreenshotWriteError();
        }

        _output.WriteLine($"ok {command.OutputPath}");
        return 0;
    }

    private async Task RequireCoordinateAsync(int x, int y, CancellationToken cancellationToken)
    {
        SessionStatus status = await ReadStatusAsync(cancellationToken);
        try
        {
            SessionToolArguments.RequireCoordinate(x, y, status.Screen);
        }
        catch (ArgumentOutOfRangeException error)
        {
            throw SessionToolArguments.CoordinateOutOfRange(error);
        }
    }

    private async Task<SessionStatus> ReadStatusAsync(CancellationToken cancellationToken)
    {
        SessionStatus status = await _client.GetStatusAsync(cancellationToken);
        try
        {
            ArgumentNullException.ThrowIfNull(status);
            status.Validate();
            _ = SessionStateNames.FromWireName(status.State);
        }
        catch (ArgumentException)
        {
            throw new SessionOperationException(
                SessionErrorCode.IpcProtocol,
                "The session owner reported a status that does not match the wire contract.",
                false);
        }

        return status;
    }

    private static string FormatStatus(SessionStatus status)
    {
        string line =
            $"state={status.State} sessionId={status.SessionId} authTier={AuthTierNames.ToWireName(status.AuthTier)} gateStrength={GateStrengthNames.ToWireName(status.GateStrength)} screen={status.Screen.Width}x{status.Screen.Height}";
        return status.HelloHint is null ? line : $"{line} helloHint={status.HelloHint}";
    }

    private int WriteOk()
    {
        _output.WriteLine("ok");
        return 0;
    }

    private int WriteError(SessionErrorCode code, string message)
    {
        _error.WriteLine($"{SessionErrorCodeNames.ToWireName(code)} {AsSingleLine(message)}");
        return FailureExitCode;
    }

    private static string AsSingleLine(string message)
    {
        return message.Replace('\r', ' ').Replace('\n', ' ');
    }

    private static SessionOperationException ScreenshotWriteError()
    {
        return new SessionOperationException(SessionErrorCode.InputInvalid, ScreenshotWriteFailure, false);
    }
}