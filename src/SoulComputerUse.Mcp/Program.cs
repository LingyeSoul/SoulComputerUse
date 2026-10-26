using System;

namespace SoulComputerUse.Mcp;

/// <summary>
/// Entry point of the MCP stdio adapter. The tool surface lands with milestone M3;
/// the skeleton only proves that this component builds and links against Core.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.Error.WriteLine($"SoulComputerUse.Mcp is not implemented yet; argument count = {args.Length}.");
        return 1;
    }
}