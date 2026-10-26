using System;
using SoulComputerUse.Windows.Ipc;
using SoulComputerUse.Windows.SingleInstance;

namespace SoulComputerUse.Windows.Hosting;

/// <summary>
/// The immutable inputs of one session owner: the machine-wide lock name, the owner pipe name, the
/// ownership record store, the handler behind the pipe, and the identity the record describes.
/// </summary>
/// <remarks>
/// Production code obtains these through <see cref="SessionOwnerService.ForCurrentProcess"/>, which
/// pins the contract lock and pipe names, generates a UUIDv4 session id, reads the current process
/// start time and uses the current account's store. Tests pass a unique lock name, a unique pipe
/// name and a store rooted in a temporary directory, so no test can contend with a real session.
/// </remarks>
public sealed record SessionOwnerOptions(
    string LockName,
    string PipeName,
    SessionOwnerStore Store,
    ISessionToolHandler Handler,
    string SessionId,
    int HostPid,
    DateTimeOffset HostProcessStartTime,
    DateTimeOffset AcquiredAt);