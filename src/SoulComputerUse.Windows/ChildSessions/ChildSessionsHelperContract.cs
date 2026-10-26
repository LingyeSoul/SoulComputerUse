using System;

namespace SoulComputerUse.Windows.ChildSessions;

/// <summary>The one verb the elevation helper accepts.</summary>
public enum ChildSessionsHelperVerb
{
    /// <summary>Reports the current child-session state without changing it.</summary>
    Probe = 0,

    /// <summary>Turns the machine-wide child-session capability on.</summary>
    Enable = 1,
}

/// <summary>
/// The helper's contract: one verb in, one exit code out. The helper does nothing else, which keeps
/// its elevated surface as small as an executable can be.
/// </summary>
public static class ChildSessionsHelperContract
{
    /// <summary>The only command line switch the helper recognises.</summary>
    public const string VerbSwitch = "--verb";

    /// <summary>Exit code for "the requested state is now in effect".</summary>
    public const int SuccessExitCode = 0;

    /// <summary>Exit code for "the system refused the change".</summary>
    public const int SystemRefusedExitCode = 3;

    /// <summary>Exit code for "the arguments were not the single allowed verb".</summary>
    public const int BadArgumentsExitCode = 64;

    /// <summary>Maps a verb to its command line word.</summary>
    public static string ToArgument(ChildSessionsHelperVerb verb)
    {
        return verb switch
        {
            ChildSessionsHelperVerb.Probe => "probe",
            ChildSessionsHelperVerb.Enable => "enable",
            _ => throw new ArgumentOutOfRangeException(nameof(verb), verb, "Unknown helper verb."),
        };
    }

    /// <summary>Parses exactly one allowed verb. Anything else is a bad argument, never a guess.</summary>
    public static ChildSessionsHelperVerb? ParseArguments(string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (arguments.Length != 2 || !string.Equals(arguments[0], VerbSwitch, StringComparison.Ordinal))
        {
            return null;
        }

        return arguments[1] switch
        {
            "probe" => ChildSessionsHelperVerb.Probe,
            "enable" => ChildSessionsHelperVerb.Enable,
            _ => null,
        };
    }
}