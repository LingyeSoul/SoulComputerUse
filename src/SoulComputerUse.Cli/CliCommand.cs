using System;
using System.Collections.Generic;
using SoulComputerUse.Core.Protocol;

namespace SoulComputerUse.Cli;

/// <summary>
/// One parsed CLI command. Only the members named by <see cref="Kind"/> are meaningful; the parser
/// fills the remaining members with neutral values so the record stays immutable and allocation
/// free after parsing.
/// </summary>
/// <param name="Kind">The command to run.</param>
/// <param name="X">The remote x coordinate for <see cref="CliCommandKind.Move"/>, <see cref="CliCommandKind.Click"/> and <see cref="CliCommandKind.Scroll"/>.</param>
/// <param name="Y">The remote y coordinate for <see cref="CliCommandKind.Move"/>, <see cref="CliCommandKind.Click"/> and <see cref="CliCommandKind.Scroll"/>.</param>
/// <param name="DeltaX">The horizontal wheel delta for <see cref="CliCommandKind.Scroll"/>.</param>
/// <param name="DeltaY">The vertical wheel delta for <see cref="CliCommandKind.Scroll"/>.</param>
/// <param name="Button">The mouse button for <see cref="CliCommandKind.Click"/>.</param>
/// <param name="Count">The click count for <see cref="CliCommandKind.Click"/>.</param>
/// <param name="Keys">The key combination for <see cref="CliCommandKind.Key"/>; empty for other commands.</param>
/// <param name="Text">The verbatim text for <see cref="CliCommandKind.Type"/>; empty for other commands.</param>
/// <param name="OutputPath">The optional output path for <see cref="CliCommandKind.Shot"/>.</param>
internal sealed record CliCommand(
    CliCommandKind Kind,
    int X,
    int Y,
    int DeltaX,
    int DeltaY,
    MouseButton Button,
    int Count,
    IReadOnlyList<SessionKey> Keys,
    string Text,
    string? OutputPath);