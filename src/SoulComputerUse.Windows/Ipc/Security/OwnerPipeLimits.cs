namespace SoulComputerUse.Windows.Ipc.Security;

/// <summary>
/// The bounds and shared messages of one owner pipe frame. The limit counts the UTF-8 bytes of one
/// line, without its <c>\n</c> terminator, and applies in both directions.
/// </summary>
public static class OwnerPipeLimits
{
    /// <summary>The largest frame line, in UTF-8 bytes, that either side accepts.</summary>
    /// <remarks>
    /// The contract caps one encoded PNG screenshot at 5,242,880 bytes. Base64 turns every three bytes
    /// into four characters, so that payload becomes 6,990,508 bytes before any JSON; the envelope,
    /// the field names and the padding add only a few hundred more. 8 MiB is 8,388,608 bytes, about
    /// 1.3 MiB above that frame, so a contract-valid screenshot always fits while a line is still
    /// bounded. The limit is enforced while the line is read, so an oversized line is refused after at
    /// most <see cref="MaximumFrameBytes"/> buffered bytes instead of being materialized in full.
    /// </remarks>
    public const int MaximumFrameBytes = 8 * 1024 * 1024;

    /// <summary>The message both sides report when a frame exceeds <see cref="MaximumFrameBytes"/>.</summary>
    public const string ExceededMessage =
        "The owner pipe frame exceeded the maximum frame size, so the connection is closed.";
}
