using System;
using SoulComputerUse.Core.Session;

namespace SoulComputerUse.Windows.Hosting.Startup;

/// <summary>Reads the credential gate tier of this machine once, without a side effect.</summary>
/// <remarks>
/// The port exists so a test can pin a tier without probing Hello or the session's logon token, and so
/// the startup path never calls a Win32 API directly. The probe must not prompt and must not change any
/// machine state; returning a tier is its whole effect.
/// </remarks>
public interface IAuthTierSource
{
    /// <summary>Probes the tier of this machine and this session.</summary>
    /// <exception cref="Exception">The tier could not be probed.</exception>
    AuthTier Detect();
}
