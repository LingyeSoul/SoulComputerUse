using System;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.ChildSessions;
using SoulComputerUse.Windows.Identity;
using SoulComputerUse.Windows.Rdp;
using SoulComputerUse.Windows.SessionLifecycle.Teardown;
using SoulComputerUse.Windows.SingleInstance;

namespace SoulComputerUse.Windows.Hosting.Startup.Production;

/// <summary>The contract's single-instance lock, as the startup pipeline's acquire port.</summary>
/// <remarks>
/// The port is a thin adapter on purpose: it makes exactly one non-waiting attempt and hands back the
/// handle that keeps the lock alive. It never probes and never retries, so a second session can never be
/// admitted by this path.
/// </remarks>
public sealed class ProductionLockAcquire : ISingleInstanceLockAcquire
{
    /// <inheritdoc />
    public LockAttempt Acquire()
    {
        SingleInstanceLock acquired = SingleInstanceLock.Acquire();
        return acquired.IsHeld ? LockAttempt.Acquired(acquired) : LockAttempt.HeldElsewhere();
    }
}

/// <summary>The leftover sweep over the real machine session table.</summary>
public sealed class ProductionLeftoverSweep : ILeftoverSweep
{
    /// <inheritdoc />
    /// <remarks>
    /// The settle really waits: Windows finishes a logoff asynchronously, and a session that is still
    /// listed a moment after a successful request is not a leftover that survived. The account the sweep
    /// runs as is read from the session table, in the adapter, so the pipeline itself stays free of
    /// environment access; the child sessions of this product always run as that account.
    /// </remarks>
    public LeftoverSweepReport Sweep(int recordedChildSessionId)
    {
        WtsSessionTable sessionTable = new();
        return new LeftoverSweeper(sessionTable, new ThreadLogoffSettle()).Sweep(
            new LeftoverEvidence(recordedChildSessionId, sessionTable.CurrentSessionAccountName()));
    }
}

/// <summary>The machine child-session state, as the startup pipeline's gate port.</summary>
/// <remarks>
/// Probing and enabling are separate on purpose: the pipeline decides from the probe alone, and enabling
/// always goes through the minimal elevation helper, which derives its own path and accepts one verb.
/// </remarks>
public sealed class ProductionChildSessionsGate : IChildSessionsGate
{
    private readonly string _applicationDirectory;
    private readonly int _elevationTimeoutMilliseconds;

    /// <summary>Creates the gate over the real machine state and the helper beside this program.</summary>
    /// <exception cref="ArgumentException">The application directory is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The timeout is not positive.</exception>
    public ProductionChildSessionsGate(string applicationDirectory, int elevationTimeoutMilliseconds)
    {
        if (string.IsNullOrWhiteSpace(applicationDirectory))
        {
            throw new ArgumentException("The application directory must not be empty.", nameof(applicationDirectory));
        }

        if (elevationTimeoutMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(elevationTimeoutMilliseconds),
                elevationTimeoutMilliseconds,
                "The elevation timeout must be positive.");
        }

        _applicationDirectory = applicationDirectory;
        _elevationTimeoutMilliseconds = elevationTimeoutMilliseconds;
    }

    /// <inheritdoc />
    public bool IsEnabled()
    {
        return ChildSessionsProbe.IsEnabled();
    }

    /// <inheritdoc />
    public PolicyState ReadPolicy(PolicyValue value)
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown child-session policy value.");
        }

        string valueName = value switch
        {
            PolicyValue.RdpInbound => ChildSessionsPolicy.RdpInboundValueName,
            PolicyValue.ChildConnections => ChildSessionsPolicy.ChildConnectionsValueName,
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown child-session policy value."),
        };

        ChildSessionHardPreconditions preconditions = ChildSessionsProbe.ReadHardPreconditions();
        return value == PolicyValue.RdpInbound ? preconditions.RdpInbound : preconditions.ChildConnections;
    }

    /// <inheritdoc />
    public ElevationResult Enable()
    {
        return ChildSessionsElevator.EnableChildSessions(_applicationDirectory, _elevationTimeoutMilliseconds);
    }
}

/// <summary>
/// The credential gate tier of this machine, as the startup pipeline's probe port.
/// </summary>
/// <remarks>
/// The Hello verdict is supplied by the caller because this layer does not implement the Hello
/// availability query. The delegate must report a real system verdict and must never default to true:
/// claiming an enrolled Hello without one would assert a strong gate that may not exist.
/// </remarks>
public sealed class ProductionAuthTierSource : IAuthTierSource
{
    private readonly Func<bool> _helloAvailableAndEnrolled;

    /// <summary>Creates the source around the caller's Hello verdict.</summary>
    /// <exception cref="ArgumentNullException">The Hello probe is null.</exception>
    public ProductionAuthTierSource(Func<bool> helloAvailableAndEnrolled)
    {
        ArgumentNullException.ThrowIfNull(helloAvailableAndEnrolled);
        _helloAvailableAndEnrolled = helloAvailableAndEnrolled;
    }

    /// <inheritdoc />
    public AuthTier Detect()
    {
        return AuthTierProbe.DetectMachine(_helloAvailableAndEnrolled);
    }
}