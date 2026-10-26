using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;

namespace SoulComputerUse.Cli;

/// <summary>
/// Turns one command line into a <see cref="CliCommand"/>. Both entry points are pure: they either
/// return a command or throw <see cref="CliParseException"/>; they never touch the session or a
/// console.
/// </summary>
internal static class CliCommandParser
{
    private const string TypeNeedsText = "The 'type' command needs text.";
    private const string UnknownCommand = "Unknown command; run 'scu --help' to list the commands.";

    private static readonly string ButtonNames =
        string.Join(", ", Enum.GetValues<MouseButton>().Select(MouseButtonNames.ToWireName));

    private static readonly string KeyNames =
        string.Join(", ", Enum.GetValues<SessionKey>().Select(SessionKeyNames.ToWireName));

    /// <summary>Parses the arguments of a single command, without the process name.</summary>
    /// <exception cref="CliParseException">The command, an option, or an argument is invalid.</exception>
    internal static CliCommand Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0)
        {
            throw Invalid("A command is required.");
        }

        return args[0] switch
        {
            "status" => CreateWithoutArguments(CliCommandKind.Status, args, "The 'status' command takes no arguments."),
            "shot" => CreateScreenshot(args),
            "move" => CreateMove(args),
            "click" => CreateClick(args),
            "scroll" => CreateScroll(args),
            "type" => CreateType(args),
            "key" => CreateKey(args),
            "quit" => CreateWithoutArguments(CliCommandKind.Quit, args, "The 'quit' command takes no arguments."),
            "--help" => CreateWithoutArguments(CliCommandKind.Help, args, "The '--help' option takes no arguments."),
            _ => throw Invalid(UnknownCommand),
        };
    }

    /// <summary>
    /// Parses one interactive line. Whitespace separates tokens, except that everything after
    /// <c>type</c> is the verbatim text, so spaces inside the text survive.
    /// </summary>
    /// <exception cref="CliParseException">The line, an option, or an argument is invalid.</exception>
    internal static CliCommand ParseLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        string trimmed = line.Trim();
        if (trimmed.Length == 0)
        {
            throw Invalid("A command is required.");
        }

        int separator = IndexOfWhitespace(trimmed);
        if (separator >= 0 && string.Equals(trimmed[..separator], "type", StringComparison.Ordinal))
        {
            string text = trimmed[(separator + 1)..].TrimStart();
            if (text.Length == 0)
            {
                throw Invalid(TypeNeedsText);
            }

            return CreateTypeCommand(text);
        }

        string[] tokens = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return Parse(tokens);
    }

    private static int IndexOfWhitespace(string value)
    {
        for (int index = 0; index < value.Length; index++)
        {
            if (char.IsWhiteSpace(value[index]))
            {
                return index;
            }
        }

        return -1;
    }

    private static CliCommand CreateWithoutArguments(CliCommandKind kind, string[] args, string message)
    {
        if (args.Length != 1)
        {
            throw Invalid(message);
        }

        return CreateEmpty(kind);
    }

    private static CliCommand CreateScreenshot(string[] args)
    {
        if (args.Length == 1)
        {
            return CreateEmpty(CliCommandKind.Shot);
        }

        if (!string.Equals(args[1], "--out", StringComparison.Ordinal))
        {
            throw Invalid("Unknown option for 'shot'; only '--out <path>' is accepted.");
        }

        if (args.Length != 3)
        {
            throw Invalid("The '--out' option takes exactly one path.");
        }

        string path = args[2];
        if (string.IsNullOrWhiteSpace(path))
        {
            throw Invalid("The '--out' option needs a non-empty path.");
        }

        if (path.Contains('\r') || path.Contains('\n'))
        {
            throw Invalid("The output path must not contain line breaks.");
        }

        return CreateEmpty(CliCommandKind.Shot) with { OutputPath = path };
    }

    private static CliCommand CreateMove(string[] args)
    {
        if (args.Length != 3)
        {
            throw Invalid("The 'move' command takes x and y.");
        }

        return CreateEmpty(CliCommandKind.Move) with
        {
            X = ParseCoordinate(args[1], "x"),
            Y = ParseCoordinate(args[2], "y"),
        };
    }

    private static CliCommand CreateClick(string[] args)
    {
        if (args.Length < 3)
        {
            throw Invalid("The 'click' command needs x and y coordinates.");
        }

        int x = ParseCoordinate(args[1], "x");
        int y = ParseCoordinate(args[2], "y");
        MouseButton button = MouseButton.Left;
        bool hasButton = false;
        int count = 1;
        bool hasCount = false;

        int index = 3;
        while (index < args.Length)
        {
            switch (args[index])
            {
                case "--button":
                    if (hasButton)
                    {
                        throw Invalid("The '--button' option was supplied more than once.");
                    }

                    if (index + 1 >= args.Length)
                    {
                        throw Invalid($"The '--button' option needs one of: {ButtonNames}.");
                    }

                    button = ParseButton(args[index + 1]);
                    hasButton = true;
                    index += 2;
                    break;

                case "--count":
                    if (hasCount)
                    {
                        throw Invalid("The '--count' option was supplied more than once.");
                    }

                    if (index + 1 >= args.Length)
                    {
                        throw Invalid("The '--count' option needs a value between 1 and 3.");
                    }

                    count = ParseCount(args[index + 1]);
                    hasCount = true;
                    index += 2;
                    break;

                default:
                    throw Invalid("Unknown option for 'click'; it accepts '--button' and '--count'.");
            }
        }

        if (!hasButton)
        {
            throw Invalid("The 'click' command requires the '--button' option.");
        }

        if (!hasCount)
        {
            throw Invalid("The 'click' command requires the '--count' option.");
        }

        return CreateEmpty(CliCommandKind.Click) with
        {
            X = x,
            Y = y,
            Button = button,
            Count = count,
        };
    }

    private static CliCommand CreateScroll(string[] args)
    {
        if (args.Length != 5)
        {
            throw Invalid("The 'scroll' command takes x, y, deltaX and deltaY.");
        }

        return CreateEmpty(CliCommandKind.Scroll) with
        {
            X = ParseCoordinate(args[1], "x"),
            Y = ParseCoordinate(args[2], "y"),
            DeltaX = ParseDelta(args[3], "deltaX"),
            DeltaY = ParseDelta(args[4], "deltaY"),
        };
    }

    private static CliCommand CreateType(string[] args)
    {
        if (args.Length < 2)
        {
            throw Invalid(TypeNeedsText);
        }

        return CreateTypeCommand(string.Join(' ', args, 1, args.Length - 1));
    }

    private static CliCommand CreateTypeCommand(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length > SessionToolArguments.MaximumTextLength)
        {
            throw Invalid($"The text length must not exceed {SessionToolArguments.MaximumTextLength} characters.");
        }

        return CreateEmpty(CliCommandKind.Type) with { Text = text };
    }

    private static CliCommand CreateKey(string[] args)
    {
        if (args.Length != 2)
        {
            throw Invalid("The 'key' command takes one combination such as ctrl+alt+delete.");
        }

        string[] names = args[1].Split('+');
        if (names.Length > SessionToolArguments.MaximumKeyCount)
        {
            throw Invalid($"A key combination must hold between 1 and {SessionToolArguments.MaximumKeyCount} keys.");
        }

        List<SessionKey> keys = new(names.Length);
        foreach (string name in names)
        {
            if (name.Length == 0)
            {
                throw Invalid($"A key name must be one of: {KeyNames}.");
            }

            keys.Add(ParseKey(name));
        }

        return CreateEmpty(CliCommandKind.Key) with { Keys = keys.AsReadOnly() };
    }

    private static CliCommand CreateEmpty(CliCommandKind kind)
    {
        return new CliCommand(
            kind,
            0,
            0,
            0,
            0,
            MouseButton.Left,
            1,
            Array.Empty<SessionKey>(),
            string.Empty,
            null);
    }

    private static int ParseCoordinate(string value, string fieldName)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int coordinate))
        {
            throw Invalid($"The {fieldName} coordinate must be an integer.");
        }

        if (coordinate < 0)
        {
            throw new CliParseException(
                SessionErrorCode.CoordOutOfRange,
                $"The {fieldName} coordinate must be zero or greater.");
        }

        return coordinate;
    }

    private static int ParseDelta(string value, string fieldName)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int delta))
        {
            throw Invalid($"The {fieldName} value must be an integer.");
        }

        return delta;
    }

    private static int ParseCount(string value)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count))
        {
            throw Invalid("The click count must be an integer.");
        }

        if (count is < 1 or > SessionToolArguments.MaximumClickCount)
        {
            throw Invalid($"The click count must satisfy 1 <= count <= {SessionToolArguments.MaximumClickCount}.");
        }

        return count;
    }

    private static MouseButton ParseButton(string value)
    {
        try
        {
            return MouseButtonNames.FromWireName(value);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw Invalid($"The button must be one of: {ButtonNames}.");
        }
    }

    private static SessionKey ParseKey(string value)
    {
        try
        {
            return SessionKeyNames.FromWireName(value);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw Invalid($"A key name must be one of: {KeyNames}.");
        }
    }

    private static CliParseException Invalid(string message)
    {
        return new CliParseException(SessionErrorCode.InputInvalid, message);
    }
}