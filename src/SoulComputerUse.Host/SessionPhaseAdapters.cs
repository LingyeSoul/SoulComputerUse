using System;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Hosting.Startup.Phases;
using SoulComputerUse.Windows.Hosting.Startup.Split;

namespace SoulComputerUse.Host;

/// <summary>
/// Runs the before-connect segment and keeps its result, so the later phases can use the lock handle and
/// the child-session marker the segment produced.
/// </summary>
/// <remarks>
/// The phase runner hands the lock handle to the caller in its report but does not expose the rest of the
/// segment's result. Keeping it here is what lets the after-logon segment be built after the first phase
/// instead of before it, and what lets the shutdown plan know whether this session enabled the machine
/// switch.
/// </remarks>
internal sealed class CapturingBeforeConnect : ISessionStartBeforeConnect
{
    private readonly BeforeConnectSegment _segment;

    internal CapturingBeforeConnect(BeforeConnectSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        _segment = segment;
    }

    /// <summary>The completed first segment, or null before it ran.</summary>
    internal BeforeConnectResult? Result { get; private set; }

    /// <summary>The credential tier the segment probed, or null before it ran.</summary>
    internal AuthTier? AuthTier => _segment.AuthTier;

    /// <inheritdoc />
    public BeforeConnectResult Execute()
    {
        BeforeConnectResult result = _segment.Execute();
        Result = result;
        return result;
    }
}

/// <summary>
/// Builds the after-logon segment at the moment the phase runner reaches it, when the before-connect result
/// exists.
/// </summary>
/// <remarks>
/// The segment needs the before-connect result at construction because that result carries the lock handle
/// and the switch marker. Constructing it earlier would mean passing a placeholder, which is exactly the
/// kind of "fill it in later" wiring that lets a session record claim facts nobody observed.
/// </remarks>
internal sealed class DeferredAfterLogon : ISessionStartAfterLogon
{
    private readonly CapturingBeforeConnect _beforeConnect;
    private readonly Func<BeforeConnectResult, AfterLogonSegment> _factory;

    internal DeferredAfterLogon(
        CapturingBeforeConnect beforeConnect,
        Func<BeforeConnectResult, AfterLogonSegment> factory)
    {
        ArgumentNullException.ThrowIfNull(beforeConnect);
        ArgumentNullException.ThrowIfNull(factory);
        _beforeConnect = beforeConnect;
        _factory = factory;
    }

    /// <summary>The segment that ran, or null before this phase was reached.</summary>
    internal AfterLogonSegment? Segment { get; private set; }

    /// <inheritdoc />
    public AfterLogonResult Execute()
    {
        BeforeConnectResult beforeConnect = _beforeConnect.Result
            ?? throw new InvalidOperationException(
                "The after-logon phase was reached without a completed before-connect result, so no lock or switch marker is known.");

        AfterLogonSegment segment = _factory(beforeConnect);
        Segment = segment;
        return segment.Execute();
    }
}