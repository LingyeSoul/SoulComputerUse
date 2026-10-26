using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SoulComputerUse.Windows.Native;

// The named-lock entry points below exist so tests can take a lock without ever contending with a
// real session for the machine-wide name. They are internal on purpose: a public "acquire this name"
// entry point would be a way to take a second lock alongside the contract-pinned one.
[assembly: InternalsVisibleTo("SoulComputerUse.IntegrationTests")]
[assembly: InternalsVisibleTo("SoulComputerUse.TestSupport")]

namespace SoulComputerUse.Windows.SingleInstance;

/// <summary>Result of trying to become the one machine-wide session owner.</summary>
public enum LockAcquireOutcome
{
    /// <summary>This process created the lock and now owns it.</summary>
    Acquired = 0,

    /// <summary>Another process already holds the lock.</summary>
    HeldElsewhere = 1,
}

/// <summary>
/// The machine-wide single-instance lock. Exactly one process on the machine may hold it, no matter
/// which Windows account or which entry point (GUI, CLI, MCP) started first.
/// </summary>
/// <remarks>
/// Occupancy model: the lock object's lifetime expresses occupancy. The object is created without
/// ownership, <c>ERROR_ALREADY_EXISTS</c> means another process holds it, and releasing is closing the
/// handle, which any thread may do. This deliberately avoids <c>WaitOne</c> / <c>ReleaseMutex</c>,
/// whose ownership belongs to the acquiring thread and cannot be released from a shutdown thread.
/// <para>
/// Because no thread ever owns the object, a dead owner leaves no abandonment event behind: the next
/// process simply creates the object. Leftover recovery is therefore driven by the ownership record
/// on every successful acquisition.
/// </para>
/// <para>
/// Probing uses <c>OpenMutexW</c> and never creates the object: creating it and closing the handle
/// again would briefly occupy the machine-wide lock and leave a window in which a second session sees
/// no owner.
/// </para>
/// </remarks>
public sealed class SingleInstanceLock : IDisposable
{
    /// <summary>
    /// The contract-pinned mutex name. It is an identity, not a label: never regenerate it, never
    /// derive it from a product name, and never "fix" it per user.
    /// </summary>
    public const string MutexName = @"Global\SoulComputerUse-{4C1F0E2A-9B7D-4E8A-A6C3-5D9E0F1A2B34}";

    private static readonly object OwnerGate = new();
    private static SingleInstanceLock? _processOwner;

    private readonly LockAcquireOutcome _outcome;
    private readonly string _lockName;
    private nint _handle;
    private bool _disposed;

    private SingleInstanceLock(LockAcquireOutcome outcome, nint handle, string lockName)
    {
        _outcome = outcome;
        _handle = handle;
        _lockName = lockName;
    }

    /// <summary>What happened when this instance tried to take the lock.</summary>
    public LockAcquireOutcome Outcome => _outcome;

    /// <summary>True when this instance owns the lock and must release it on dispose.</summary>
    public bool IsHeld => _outcome == LockAcquireOutcome.Acquired;

    /// <summary>
    /// Reports whether the machine-wide lock object exists, without creating it. A client that only
    /// wants to attach to an existing owner must use this.
    /// </summary>
    public static bool IsHeldByAnyone()
    {
        return IsHeldByAnyone(MutexName);
    }

    /// <summary>
    /// Reports whether the named lock object exists, without creating it. Internal so that only test
    /// assemblies can probe a name of their own.
    /// </summary>
    internal static bool IsHeldByAnyone(string lockName)
    {
        ArgumentException.ThrowIfNullOrEmpty(lockName);

        nint handle = NativeMethods.OpenMutexW(NativeMethods.Synchronize, false, lockName);
        if (handle == 0)
        {
            int errorCode = Marshal.GetLastWin32Error();
            if (errorCode == NativeMethods.ErrorFileNotFound)
            {
                return false;
            }

            throw new InvalidOperationException(
                $"OpenMutexW for the session lock failed with Win32 error {errorCode}, so the lock state cannot be determined.",
                new Win32Exception(errorCode));
        }

        NativeMethods.CloseHandle(handle);
        return true;
    }

    /// <summary>Takes the machine-wide lock, which is the only lock a production entry point uses.</summary>
    public static SingleInstanceLock Acquire()
    {
        return Acquire(MutexName);
    }

    /// <summary>
    /// Takes the named lock without waiting. Internal so a test can use a per-run name and never
    /// contend with a real session for the machine-wide lock.
    /// </summary>
    internal static SingleInstanceLock Acquire(string lockName)
    {
        ArgumentException.ThrowIfNullOrEmpty(lockName);

        lock (OwnerGate)
        {
            // Guarding inside the process as well: two holders in one process would be just as wrong
            // as two processes, and a second handle would keep the object alive after shutdown. The
            // guard is per lock name, so one name being held never hides another.
            if (_processOwner is not null && string.Equals(_processOwner._lockName, lockName, StringComparison.Ordinal))
            {
                return new SingleInstanceLock(LockAcquireOutcome.HeldElsewhere, 0, lockName);
            }

            nint handle = NativeMethods.CreateMutexW(0, false, lockName);
            if (handle == 0)
            {
                int errorCode = Marshal.GetLastWin32Error();
                throw new InvalidOperationException(
                    $"CreateMutexW for the session lock failed with Win32 error {errorCode}.",
                    new Win32Exception(errorCode));
            }

            int creationError = Marshal.GetLastWin32Error();
            if (creationError == NativeMethods.ErrorAlreadyExists)
            {
                NativeMethods.CloseHandle(handle);
                return new SingleInstanceLock(LockAcquireOutcome.HeldElsewhere, 0, lockName);
            }

            SingleInstanceLock owner = new(LockAcquireOutcome.Acquired, handle, lockName);
            _processOwner = owner;
            return owner;
        }
    }

    /// <summary>Releases the lock by closing its handle, from any thread. Idempotent.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (OwnerGate)
        {
            if (ReferenceEquals(_processOwner, this))
            {
                _processOwner = null;
            }
        }

        nint handle = _handle;
        _handle = 0;
        if (handle == 0)
        {
            return;
        }

        if (!NativeMethods.CloseHandle(handle))
        {
            int errorCode = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                $"CloseHandle for the session lock '{_lockName}' failed with Win32 error {errorCode}, so the lock may still be held.",
                new Win32Exception(errorCode));
        }
    }
}