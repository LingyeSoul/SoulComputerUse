using System;
using System.ComponentModel;
using SoulComputerUse.Windows.ChildSessions;

namespace SoulComputerUse.ChildSessionsHelper;

/// <summary>
/// The minimal elevation helper. It accepts exactly one verb and either reports the child-session
/// state or turns it on; it never listens, never stays resident and writes no file, which is what
/// keeps its elevated surface as small as possible.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        ChildSessionsHelperVerb? verb = ChildSessionsHelperContract.ParseArguments(args);
        if (verb is null)
        {
            Console.Error.WriteLine(
                $"usage: {ChildSessionsHelperContract.VerbSwitch} {ChildSessionsHelperContract.ToArgument(ChildSessionsHelperVerb.Probe)}|{ChildSessionsHelperContract.ToArgument(ChildSessionsHelperVerb.Enable)}");
            return ChildSessionsHelperContract.BadArgumentsExitCode;
        }

        try
        {
            switch (verb.Value)
            {
                case ChildSessionsHelperVerb.Probe:
                    Console.Out.WriteLine(ChildSessionsProbe.IsEnabled() ? "enabled" : "disabled");
                    return ChildSessionsHelperContract.SuccessExitCode;
                case ChildSessionsHelperVerb.Enable:
                    ChildSessionsProbe.SetEnabled(true);
                    return ChildSessionsHelperContract.SuccessExitCode;
                default:
                    Console.Error.WriteLine($"unsupported verb: {verb}");
                    return ChildSessionsHelperContract.BadArgumentsExitCode;
            }
        }
        catch (Win32Exception error)
        {
            // The system refused the change. Report the code only: this helper logs no paths.
            Console.Error.WriteLine($"the system refused the change (Win32 error {error.NativeErrorCode})");
            return ChildSessionsHelperContract.SystemRefusedExitCode;
        }
        catch (InvalidOperationException error)
        {
            Console.Error.WriteLine(error.Message);
            return ChildSessionsHelperContract.SystemRefusedExitCode;
        }
    }
}