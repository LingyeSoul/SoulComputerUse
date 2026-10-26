using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;

namespace SoulComputerUse.Windows.Ipc;

/// <summary>Typed arguments of <c>mouse_move</c>.</summary>
public sealed record MouseMoveArguments(int X, int Y);

/// <summary>Typed arguments of <c>mouse_click</c>.</summary>
public sealed record MouseClickArguments(int X, int Y, MouseButton Button, int Count);

/// <summary>Typed arguments of <c>mouse_scroll</c>.</summary>
public sealed record MouseScrollArguments(int X, int Y, int DeltaX, int DeltaY);

/// <summary>Typed arguments of <c>key_type</c>.</summary>
public sealed record KeyTypeArguments(string Text);

/// <summary>Typed arguments of <c>key_press</c>.</summary>
public sealed record KeyPressArguments(IReadOnlyList<SessionKey> Keys);

/// <summary>
/// The strict argument and result codecs of the seven tools. Each reader refuses a missing, mistyped or
/// extra field with <c>E_INPUT_INVALID</c>, matching the published JSON Schema of section 5 of the
/// interface contract; nothing is coerced.
/// </summary>
/// <remarks>
/// Coordinate bounds are not checked here. They belong to the owner, which knows the remote screen,
/// and are reported as <c>E_COORD_OUT_OF_RANGE</c> by the owner instead of being guessed by a client.
/// </remarks>
public static class SessionToolPayloads
{
    private const string PngBase64Name = "pngBase64";
    private const string OkName = "ok";

    /// <summary>Returns the empty argument object the no-argument tools send.</summary>
    public static JsonElement EmptyArguments()
    {
        using JsonDocument document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    /// <summary>Returns the <c>{ "ok": true }</c> result the action tools share.</summary>
    public static JsonElement OkResult()
    {
        using JsonDocument document = JsonDocument.Parse("""{"ok":true}""");
        return document.RootElement.Clone();
    }

    /// <summary>Reads the arguments of <c>mouse_move</c>.</summary>
    public static MouseMoveArguments ReadMouseMove(JsonElement arguments)
    {
        RequireObject(arguments);
        RejectUnknown(arguments, ["x", "y"]);
        return new MouseMoveArguments(ReadCoordinate(arguments, "x"), ReadCoordinate(arguments, "y"));
    }

    /// <summary>Reads the arguments of <c>mouse_click</c>.</summary>
    public static MouseClickArguments ReadMouseClick(JsonElement arguments)
    {
        RequireObject(arguments);
        RejectUnknown(arguments, ["x", "y", "button", "count"]);
        int count = ReadInteger(arguments, "count");
        if (count is < 1 or > SessionToolArguments.MaximumClickCount)
        {
            throw InputInvalid("'count' must be between 1 and " + SessionToolArguments.MaximumClickCount.ToString(CultureInfo.InvariantCulture) + ".");
        }

        return new MouseClickArguments(
            ReadCoordinate(arguments, "x"),
            ReadCoordinate(arguments, "y"),
            ReadButton(arguments),
            count);
    }

    /// <summary>Reads the arguments of <c>mouse_scroll</c>.</summary>
    public static MouseScrollArguments ReadMouseScroll(JsonElement arguments)
    {
        RequireObject(arguments);
        RejectUnknown(arguments, ["x", "y", "deltaX", "deltaY"]);
        return new MouseScrollArguments(
            ReadCoordinate(arguments, "x"),
            ReadCoordinate(arguments, "y"),
            ReadInteger(arguments, "deltaX"),
            ReadInteger(arguments, "deltaY"));
    }

    /// <summary>Reads the arguments of <c>key_type</c>.</summary>
    public static KeyTypeArguments ReadKeyType(JsonElement arguments)
    {
        RequireObject(arguments);
        RejectUnknown(arguments, ["text"]);
        if (!arguments.TryGetProperty("text", out JsonElement textElement) || textElement.ValueKind != JsonValueKind.String)
        {
            throw InputInvalid("'text' must be a string.");
        }

        string text = textElement.GetString()!;
        if (text.Length > SessionToolArguments.MaximumTextLength)
        {
            throw InputInvalid("'text' must not exceed " + SessionToolArguments.MaximumTextLength.ToString(CultureInfo.InvariantCulture) + " characters.");
        }

        return new KeyTypeArguments(text);
    }

    /// <summary>Reads the arguments of <c>key_press</c>.</summary>
    public static KeyPressArguments ReadKeyPress(JsonElement arguments)
    {
        RequireObject(arguments);
        RejectUnknown(arguments, ["keys"]);
        if (!arguments.TryGetProperty("keys", out JsonElement keysElement) || keysElement.ValueKind != JsonValueKind.Array)
        {
            throw InputInvalid("'keys' must be an array of key names.");
        }

        List<SessionKey> keys = [];
        foreach (JsonElement keyElement in keysElement.EnumerateArray())
        {
            if (keyElement.ValueKind != JsonValueKind.String)
            {
                throw InputInvalid("'keys' must contain only key-name strings.");
            }

            keys.Add(ReadKey(keyElement.GetString()!));
        }

        if (keys.Count is < 1 or > SessionToolArguments.MaximumKeyCount)
        {
            throw InputInvalid("'keys' must hold between 1 and " + SessionToolArguments.MaximumKeyCount.ToString(CultureInfo.InvariantCulture) + " keys.");
        }

        return new KeyPressArguments(keys);
    }

    /// <summary>Encodes the <c>get_status</c> result payload.</summary>
    public static JsonElement WriteStatus(SessionStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        status.Validate();
        AuthTierRules.EnsureConsistent(status.AuthTier, status.GateStrength);

        JsonElement payload = Encode(writer =>
        {
            writer.WriteString("state", status.State);
            writer.WriteString("sessionId", status.SessionId);
            writer.WriteString("authTier", AuthTierNames.ToWireName(status.AuthTier));
            writer.WriteString("gateStrength", GateStrengthNames.ToWireName(status.GateStrength));
            writer.WriteStartObject("screen");
            writer.WriteNumber("width", status.Screen.Width);
            writer.WriteNumber("height", status.Screen.Height);
            writer.WriteEndObject();
            if (status.HelloHint is not null)
            {
                writer.WriteString("helloHint", status.HelloHint);
            }
        });

        return payload;
    }

    /// <summary>
    /// Reads a <c>get_status</c> result payload. Every field the contract requires is mandatory; an
    /// unknown tier, gate strength or hello hint is refused instead of being guessed.
    /// </summary>
    public static SessionStatus ReadStatus(JsonElement result)
    {
        RequireObject(result);
        RejectUnknown(result, ["state", "sessionId", "authTier", "gateStrength", "screen", "helloHint"]);
        string state = ReadString(result, "state");
        string sessionId = ReadString(result, "sessionId");
        AuthTier tier = ReadAuthTier(result);
        GateStrength strength = ReadGateStrength(result);
        SessionScreen screen = ReadScreen(result);
        string? helloHint = ReadOptionalString(result, "helloHint");

        SessionStatus status = new(state, sessionId, tier, strength, screen, helloHint);
        try
        {
            status.Validate();
            AuthTierRules.EnsureConsistent(tier, strength);
        }
        catch (ArgumentException error)
        {
            throw new IpcProtocolException("The owner reported a status payload the contract does not allow.", error);
        }

        return status;
    }

    /// <summary>Encodes one PNG frame as the base64 result the owner sends.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The frame exceeds the contract's byte limit.</exception>
    public static JsonElement WriteScreenshot(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        if (png.Length > SessionIpcRules.ScreenshotByteLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(png),
                png.Length,
                "The PNG frame exceeds the contract limit of " + SessionIpcRules.ScreenshotByteLimit.ToString(CultureInfo.InvariantCulture) + " bytes.");
        }

        JsonElement payload = Encode(writer => writer.WriteString(PngBase64Name, Convert.ToBase64String(png)));
        return payload;
    }

    /// <summary>
    /// Reads a base64 <c>screenshot</c> result. The payload must decode as base64 and must not exceed
    /// the contract's encoded byte limit.
    /// </summary>
    public static byte[] ReadScreenshot(JsonElement result)
    {
        RequireObject(result);
        RejectUnknown(result, [PngBase64Name]);
        string encoded = ReadString(result, PngBase64Name);

        byte[] png;
        try
        {
            png = Convert.FromBase64String(encoded);
        }
        catch (FormatException error)
        {
            throw new IpcProtocolException("The owner reported a screenshot payload that is not valid base64.", error);
        }

        if (png.Length > SessionIpcRules.ScreenshotByteLimit)
        {
            throw new SessionOperationException(
                SessionErrorCode.ScreenshotTooLarge,
                "The owner reported a PNG frame larger than the contract limit of "
                    + SessionIpcRules.ScreenshotByteLimit.ToString(CultureInfo.InvariantCulture)
                    + " bytes, so the frame was refused.",
                false);
        }

        return png;
    }

    private static void RequireObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw InputInvalid("The arguments must be a JSON object.");
        }
    }

    private static void RejectUnknown(JsonElement element, string[] allowedNames)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            bool allowed = false;
            foreach (string name in allowedNames)
            {
                if (string.Equals(property.Name, name, StringComparison.Ordinal))
                {
                    allowed = true;
                    break;
                }
            }

            if (!allowed)
            {
                throw InputInvalid("'" + property.Name + "' is not an argument of this tool.");
            }
        }
    }

    private static int ReadInteger(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int number))
        {
            throw InputInvalid("'" + name + "' must be a 32-bit integer.");
        }

        return number;
    }

    private static int ReadCoordinate(JsonElement element, string name)
    {
        int value = ReadInteger(element, name);
        if (value < 0)
        {
            throw new SessionOperationException(
                SessionErrorCode.CoordOutOfRange,
                "'" + name + "' must not be negative; the origin is the top-left corner.",
                false);
        }

        return value;
    }

    private static MouseButton ReadButton(JsonElement element)
    {
        string wireName = ReadString(element, "button");
        try
        {
            return MouseButtonNames.FromWireName(wireName);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw InputInvalid("'button' must be one of left, right or middle.");
        }
    }

    private static SessionKey ReadKey(string wireName)
    {
        try
        {
            return SessionKeyNames.FromWireName(wireName);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw InputInvalid("'keys' contains a key name the contract does not define.");
        }
    }

    private static string ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String)
        {
            throw InputInvalid("'" + name + "' must be a string.");
        }

        return value.GetString()!;
    }

    private static string? ReadOptionalString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw InputInvalid("'" + name + "' must be a string when it is present.");
        }

        return value.GetString();
    }

    private static AuthTier ReadAuthTier(JsonElement element)
    {
        string wireName = ReadString(element, "authTier");
        try
        {
            return AuthTierNames.FromWireName(wireName);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new IpcProtocolException("The owner reported an authentication tier the contract does not define.");
        }
    }

    private static GateStrength ReadGateStrength(JsonElement element)
    {
        string wireName = ReadString(element, "gateStrength");
        try
        {
            return GateStrengthNames.FromWireName(wireName);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new IpcProtocolException("The owner reported a gate strength the contract does not define.");
        }
    }

    private static SessionScreen ReadScreen(JsonElement element)
    {
        if (!element.TryGetProperty("screen", out JsonElement screen) || screen.ValueKind != JsonValueKind.Object)
        {
            throw InputInvalid("'screen' must be an object with width and height.");
        }

        RejectUnknown(screen, ["width", "height"]);
        return new SessionScreen(ReadInteger(screen, "width"), ReadInteger(screen, "height"));
    }

    private static SessionOperationException InputInvalid(string message)
    {
        return new SessionOperationException(SessionErrorCode.InputInvalid, message, false);
    }

    private static JsonElement Encode(Action<Utf8JsonWriter> writeProperties)
    {
        System.Buffers.ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writeProperties(writer);
            writer.WriteEndObject();
            writer.Flush();
        }

        using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }
}
