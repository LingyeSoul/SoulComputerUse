using System;
using System.Collections.Generic;
using System.Text.Json;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;

namespace SoulComputerUse.Mcp;

/// <summary>
/// Reads the arguments of one tool call against the contract's schema. Unknown names, duplicate
/// names, missing required names and values of the wrong JSON type are refused with the contract's
/// invalid-input code; the message names the field and the reason, never the supplied value.
/// </summary>
internal sealed class ToolArgumentReader
{
    private static readonly string[] NoNames = [];

    private static readonly string[] MouseMoveNames = ["x", "y"];

    private static readonly string[] MouseClickNames = ["x", "y", "button", "count"];

    private static readonly string[] MouseScrollNames = ["x", "y", "deltaX", "deltaY"];

    private static readonly string[] KeyTypeNames = ["text"];

    private static readonly string[] KeyPressNames = ["keys"];

    private static readonly ToolArgumentReader EmptyReader = new(new Dictionary<string, JsonElement>(StringComparer.Ordinal));

    private readonly Dictionary<string, JsonElement> _members;

    private ToolArgumentReader(Dictionary<string, JsonElement> members)
    {
        _members = members;
    }

    /// <summary>Reads a tool's arguments, accepting exactly the names its published schema declares.</summary>
    internal static ToolArgumentReader Read(SessionTool tool, JsonElement? arguments)
    {
        string[] acceptedNames = NamesOf(tool);
        if (arguments is null)
        {
            return acceptedNames.Length == 0
                ? EmptyReader
                : new ToolArgumentReader(new Dictionary<string, JsonElement>(StringComparer.Ordinal));
        }

        JsonElement value = arguments.Value;
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("The tool arguments must be a JSON object.");
        }

        Dictionary<string, JsonElement> members = new(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!IsAccepted(acceptedNames, property.Name))
            {
                string accepted = acceptedNames.Length == 0
                    ? "The tool takes no arguments."
                    : $"The accepted arguments are: {string.Join(", ", acceptedNames)}.";
                throw Invalid($"The argument '{property.Name}' is not accepted by this tool. {accepted}");
            }

            if (!members.TryAdd(property.Name, property.Value))
            {
                throw Invalid($"The argument '{property.Name}' is supplied more than once.");
            }
        }

        return new ToolArgumentReader(members);
    }

    /// <summary>Reads a required 32-bit signed integer.</summary>
    internal int RequireInt32(string name)
    {
        JsonElement value = Require(name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int result))
        {
            throw Invalid($"The argument '{name}' must be an integer within the 32-bit signed range.");
        }

        return result;
    }

    /// <summary>Reads a required string.</summary>
    internal string RequireString(string name)
    {
        JsonElement value = Require(name);
        if (value.ValueKind != JsonValueKind.String)
        {
            throw Invalid($"The argument '{name}' must be a string.");
        }

        return value.GetString() ?? string.Empty;
    }

    /// <summary>Reads the <c>button</c> argument as one of the contract's mouse buttons.</summary>
    internal MouseButton RequireMouseButton(string name)
    {
        string wireName = RequireString(name);
        try
        {
            return MouseButtonNames.FromWireName(wireName);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw Invalid($"The argument '{name}' must be one of: left, right, middle.");
        }
    }

    /// <summary>Reads the <c>keys</c> argument as the contract's key enumeration.</summary>
    internal IReadOnlyList<SessionKey> RequireKeys(string name)
    {
        JsonElement value = Require(name);
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw Invalid($"The argument '{name}' must be an array of key names.");
        }

        List<SessionKey> keys = [];
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw Invalid($"Every entry of the argument '{name}' must be a key name string.");
            }

            string? wireName = item.GetString();
            if (string.IsNullOrEmpty(wireName))
            {
                throw Invalid($"Every entry of the argument '{name}' must be a key name string.");
            }

            try
            {
                keys.Add(SessionKeyNames.FromWireName(wireName));
            }
            catch (ArgumentOutOfRangeException)
            {
                throw Invalid($"The argument '{name}' contains a name outside the accepted key enumeration.");
            }
        }

        try
        {
            SessionToolArguments.RequireKeys(keys);
        }
        catch (ArgumentOutOfRangeException error)
        {
            throw SessionToolArguments.InputInvalid(error);
        }

        return keys;
    }

    private JsonElement Require(string name)
    {
        if (!_members.TryGetValue(name, out JsonElement value))
        {
            throw Invalid($"The required argument '{name}' is missing.");
        }

        return value;
    }

    private static bool IsAccepted(string[] acceptedNames, string name)
    {
        for (int index = 0; index < acceptedNames.Length; index++)
        {
            if (string.Equals(acceptedNames[index], name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string[] NamesOf(SessionTool tool)
    {
        return tool switch
        {
            SessionTool.GetStatus => NoNames,
            SessionTool.Screenshot => NoNames,
            SessionTool.MouseMove => MouseMoveNames,
            SessionTool.MouseClick => MouseClickNames,
            SessionTool.MouseScroll => MouseScrollNames,
            SessionTool.KeyType => KeyTypeNames,
            SessionTool.KeyPress => KeyPressNames,
            _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, "Unknown session tool."),
        };
    }

    private static SessionOperationException Invalid(string message)
    {
        return new SessionOperationException(SessionErrorCode.InputInvalid, message, false);
    }
}
