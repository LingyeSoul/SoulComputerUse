using System;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Windows.Ipc;
using SoulComputerUse.Windows.SingleInstance;

namespace SoulComputerUse.Windows.Hosting.Headless;

/// <summary>
/// The environment gate that decides whether a CLI or MCP process may become the session owner, and
/// the production option set used when it may.
/// </summary>
/// <remarks>
/// <para>
/// The gate is one variable so the default behaviour stays exactly what it is without it: discovery
/// receives no starter, a free lock without an owner fails with <c>E_NOT_READY</c>, and nothing is
/// started or written. Only <c>SOULCOMPUTERUSE_ALLOW_HEADLESS=1</c> allows a starter; no other
/// variable is read and no other value enables it.
/// </para>
/// <para>
/// This build has no session backend to place behind a headless owner, so the entry points pass a
/// factory that returns <see cref="UnavailableSessionClient"/>. <see cref="HeadlessOwnerStarter"/>
/// refuses that backend with <c>E_NOT_READY</c> before it takes the lock, so enabling the gate does not
/// make a session appear. A host that owns a real client passes its own factory instead.
/// </para>
/// </remarks>
public static class HeadlessStarterActivation
{
    /// <summary>The only environment variable this build reads to allow a headless owner.</summary>
    public const string AllowHeadlessVariable = "SOULCOMPUTERUSE_ALLOW_HEADLESS";

    /// <summary>The only value of <see cref="AllowHeadlessVariable"/> that allows a headless owner.</summary>
    public const string AllowHeadlessValue = "1";

    /// <summary>Reports whether the environment allows this process to become the session owner.</summary>
    public static bool IsAllowed()
    {
        string? value = Environment.GetEnvironmentVariable(AllowHeadlessVariable);
        return string.Equals(value, AllowHeadlessValue, StringComparison.Ordinal);
    }

    /// <summary>
    /// Builds the production option set: the contract lock and owner pipe names, a fresh UUIDv4
    /// session id, this account's ownership record store, the standard handler adapter, and the
    /// caller-supplied session client factory.
    /// </summary>
    /// <param name="clientFactory">Builds the session client the owner pipe would execute calls through.</param>
    /// <exception cref="ArgumentNullException">The factory is null.</exception>
    /// <exception cref="InvalidOperationException">This account's ownership record store cannot be resolved.</exception>
    public static HeadlessStarterOptions ProductionOptions(Func<ISessionClient> clientFactory)
    {
        ArgumentNullException.ThrowIfNull(clientFactory);
        return new HeadlessStarterOptions(
            SingleInstanceLock.MutexName,
            IpcPipeNames.Owner,
            Guid.NewGuid().ToString("D"),
            SessionOwnerStore.ForCurrentUser(),
            static client => new SessionClientToolHandler(client),
            clientFactory);
    }
}
