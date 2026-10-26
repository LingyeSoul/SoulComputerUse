using System;

namespace SoulComputerUse.Windows.Hosting.Startup;

/// <summary>
/// One executed stage of the startup pipeline, in the only order the contract allows. The numeric
/// values are that order, and a report refuses a sequence that leaves it.
/// </summary>
public enum SessionStartStage
{
    /// <summary>Take the machine-wide single-instance lock.</summary>
    Lock = 0,

    /// <summary>Read this account's ownership record and judge whether it may be taken over.</summary>
    ReadOwnerRecord = 1,

    /// <summary>Delete a leftover record that meets the takeover criteria.</summary>
    TakeOverRecord = 2,

    /// <summary>Write the ownership record in <c>starting</c>.</summary>
    WriteStartingRecord = 3,

    /// <summary>Probe the credential gate tier once, with no side effect.</summary>
    AuthTier = 4,

    /// <summary>Log off every leftover child session and confirm they are gone.</summary>
    Sweep = 5,

    /// <summary>Probe the child-session switch and the two policy values.</summary>
    ChildSessions = 6,

    /// <summary>Ask for administrator consent and enable the child-session switch.</summary>
    EnableChildSessions = 7,

    /// <summary>Rewrite the ownership record to mark that this session enabled the switch.</summary>
    RecordChildSessions = 8,

    /// <summary>Connect the child-session agent pipe.</summary>
    ConnectAgent = 9,

    /// <summary>Rewrite the ownership record with the agent's Windows session id.</summary>
    WriteSubagentSession = 10,

    /// <summary>Start the owner pipe that carries tool calls.</summary>
    OwnerPipe = 11,

    /// <summary>Rewrite the ownership record in <c>connecting</c>.</summary>
    WriteConnecting = 12,

    /// <summary>Rewrite the ownership record in <c>ready</c>.</summary>
    WriteReady = 13,
}
