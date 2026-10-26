using System;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Windows.Ipc;
using SoulComputerUse.Windows.SingleInstance;

namespace SoulComputerUse.Windows.Hosting.Headless;

/// <summary>
/// The immutable inputs of <see cref="HeadlessOwnerStarter"/>: the lock and owner pipe it would take,
/// the session id its ownership record would carry, the record store, and the two factories that build
/// the session behind the pipe.
/// </summary>
/// <remarks>
/// Every value is supplied explicitly and both factories are required. The handler factory receives
/// the client built by the client factory, so the owner pipe and the session always share one backend,
/// and the client factory runs only when this process actually becomes the owner.
/// <para>
/// The names are parameters rather than the contract constants so a test can use a per-run lock and
/// pipe; production passes <see cref="SingleInstanceLock.MutexName"/> and <see cref="IpcPipeNames.Owner"/>.
/// </para>
/// </remarks>
public sealed record HeadlessStarterOptions
{
    /// <summary>Creates the options and refuses values no session owner could use.</summary>
    /// <param name="lockName">The single-instance lock name the starter tries to take.</param>
    /// <param name="pipeName">The owner pipe the starter serves when it becomes the owner.</param>
    /// <param name="sessionId">The session id in D-format UUID form, shared by the record and the session.</param>
    /// <param name="store">The store that holds this account's ownership record.</param>
    /// <param name="handlerFactory">Builds the owner-side handler around the client the starter created.</param>
    /// <param name="clientFactory">Builds the session client that executes the tool calls.</param>
    /// <exception cref="ArgumentException">A name is empty, the session id is not a D-format UUID, or a factory is null.</exception>
    public HeadlessStarterOptions(
        string lockName,
        string pipeName,
        string sessionId,
        SessionOwnerStore store,
        Func<ISessionClient, ISessionToolHandler> handlerFactory,
        Func<ISessionClient> clientFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockName);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        if (!Guid.TryParseExact(sessionId, "D", out Guid sessionGuid))
        {
            throw new ArgumentException("The session id must be a D-format UUID.", nameof(sessionId));
        }

        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(handlerFactory);
        ArgumentNullException.ThrowIfNull(clientFactory);

        LockName = lockName;
        PipeName = pipeName;
        SessionId = sessionGuid.ToString("D");
        Store = store;
        HandlerFactory = handlerFactory;
        ClientFactory = clientFactory;
    }

    /// <summary>The machine-wide single-instance lock the headless owner would hold.</summary>
    public string LockName { get; }

    /// <summary>The owner pipe the headless owner would serve for the CLI and the MCP adapter.</summary>
    public string PipeName { get; }

    /// <summary>The UUIDv4 session id of the headless owner, in D-format.</summary>
    public string SessionId { get; }

    /// <summary>The store the ownership record is written to and deleted from.</summary>
    public SessionOwnerStore Store { get; }

    /// <summary>Builds the owner-side handler around the client this starter created.</summary>
    public Func<ISessionClient, ISessionToolHandler> HandlerFactory { get; }

    /// <summary>Builds the session client the owner pipe executes calls through.</summary>
    public Func<ISessionClient> ClientFactory { get; }
}
