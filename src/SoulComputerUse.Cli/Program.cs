using System;

namespace SoulComputerUse.Cli;

/// <summary>
/// Entry point of the long-lived CLI adapter. The REPL lands with milestone M2;
/// the skeleton only proves that this component builds and links against Core.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.Error.WriteLine($"SoulComputerUse.Cli is not implemented yet; argument count = {args.Length}.");
        return 1;
    }
}