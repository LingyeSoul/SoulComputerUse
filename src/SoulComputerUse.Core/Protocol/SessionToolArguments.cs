using System;
using System.Collections.Generic;
using SoulComputerUse.Core.Session;

namespace SoulComputerUse.Core.Protocol;

/// <summary>
/// The argument rules shared by the CLI and the MCP adapter. Both adapters must refuse the same
/// inputs with the same reason, so the checks live here instead of being written twice.
/// </summary>
public static class SessionToolArguments
{
    /// <summary>The largest accepted text length for <c>key_type</c>.</summary>
    public const int MaximumTextLength = 1000;

    /// <summary>The largest accepted click count.</summary>
    public const int MaximumClickCount = 3;

    /// <summary>The largest accepted number of keys in one combination.</summary>
    public const int MaximumKeyCount = 5;

    /// <summary>Rejects a coordinate outside the remote screen.</summary>
    public static void RequireCoordinate(int x, int y, SessionScreen screen)
    {
        ArgumentNullException.ThrowIfNull(screen);
        screen.Validate();

        if (x < 0 || x >= screen.Width)
        {
            throw new ArgumentOutOfRangeException(
                nameof(x),
                x,
                $"The x coordinate must satisfy 0 <= x < {screen.Width}.");
        }

        if (y < 0 || y >= screen.Height)
        {
            throw new ArgumentOutOfRangeException(
                nameof(y),
                y,
                $"The y coordinate must satisfy 0 <= y < {screen.Height}.");
        }
    }

    /// <summary>Rejects a click count outside the contract's range.</summary>
    public static void RequireClickCount(int count)
    {
        if (count is < 1 or > MaximumClickCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(count),
                count,
                $"The click count must satisfy 1 <= count <= {MaximumClickCount}.");
        }
    }

    /// <summary>Rejects text longer than the contract accepts.</summary>
    public static void RequireText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length > MaximumTextLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(text),
                text.Length,
                $"The text length must not exceed {MaximumTextLength} characters.");
        }
    }

    /// <summary>Rejects an empty or oversized key combination.</summary>
    public static void RequireKeys(IReadOnlyList<SessionKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        if (keys.Count is < 1 or > MaximumKeyCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(keys),
                keys.Count,
                $"A key combination must hold between 1 and {MaximumKeyCount} keys.");
        }
    }

    /// <summary>
    /// Formats a coordinate error as the contract's error code. The message carries the boundary and
    /// the received values only; no file, account or machine data belongs here.
    /// </summary>
    public static SessionOperationException CoordinateOutOfRange(ArgumentOutOfRangeException error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new SessionOperationException(SessionErrorCode.CoordOutOfRange, error.Message, false);
    }

    /// <summary>Formats any other argument error as the contract's invalid-input code.</summary>
    public static SessionOperationException InputInvalid(ArgumentException error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new SessionOperationException(SessionErrorCode.InputInvalid, error.Message, false);
    }
}
