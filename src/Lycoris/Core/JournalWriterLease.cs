using System.IO;
using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Lycoris.Core;

/// <summary>
/// Gives one elevated process the machine journal, including its directory migration and repair.
/// A background thread holds the mutex through process shutdown, independently of the WPF dispatcher.
/// </summary>
internal sealed class JournalWriterLease : IDisposable
{
    private const string MutexName = @"Global\Lycoris.JournalWriter";
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);
    private static JournalWriterLease? _current;

    private readonly ManualResetEventSlim _release = new();
    private readonly Thread _ownerThread = Thread.CurrentThread;
    private int _disposed;

    private JournalWriterLease() { }

    internal static JournalWriterLease? Current => Volatile.Read(ref _current);

    internal static string NameFor(string? testRoot)
    {
        if (testRoot is null) return MutexName;
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(testRoot)).ToUpperInvariant();
        return $"{MutexName}.{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))}";
    }

    internal static JournalWriterLease? TryAcquire(out bool busy)
    {
        busy = false;
        return Elevation.IsElevated ? StartOwner(OpenTrustedMutex, HandshakeTimeout, out busy) : null;
    }

    internal static JournalWriterLease? TryAcquireForTests(Func<Mutex> open, TimeSpan handshakeTimeout, out bool busy) =>
        StartOwner(open, handshakeTimeout, out busy);

    private static Mutex? OpenTrustedMutex()
    {
        var mutex = MutexAcl.Create(false, NameFor(AppPaths.TestRoot), out _, CreateSecurity());
        try
        {
            if (IsTrusted(mutex.GetAccessControl())) return mutex;
            Log.Warn("Защита глобального мьютекса журнала не подтверждена.");
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
        mutex.Dispose();
        return null;
    }

    private static JournalWriterLease? StartOwner(Func<Mutex?> open, TimeSpan timeout, out bool busy)
    {
        busy = Current is not null;
        if (busy) return null;

        var ready = new TaskCompletionSource<(JournalWriterLease? Lease, bool Busy)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => Hold(open, ready)) { IsBackground = true, Name = "Lycoris journal writer" };
        thread.Start();
        if (!ready.Task.Wait(timeout))
        {
            // Serialize cancellation with publication so a late owner cannot leave a lease behind.
            lock (ready) ready.TrySetResult((null, false));
        }

        var result = ready.Task.GetAwaiter().GetResult();
        busy = result.Busy;
        return result.Lease;
    }

    private static void Hold(Func<Mutex?> open, TaskCompletionSource<(JournalWriterLease? Lease, bool Busy)> ready)
    {
        try
        {
            using var mutex = open();
            if (mutex is null || ready.Task.IsCompleted) return;
            if (!TryEnter(mutex))
            {
                ready.TrySetResult((null, true));
                return;
            }

            JournalWriterLease? lease = null;
            try
            {
                lock (ready)
                {
                    if (ready.Task.IsCompleted) return;
                    var acquired = new JournalWriterLease();
                    if (Interlocked.CompareExchange(ref _current, acquired, null) is not null)
                    {
                        acquired._release.Dispose();
                        ready.SetResult((null, true));
                        return;
                    }

                    lease = acquired;
                    ready.SetResult((lease, false));
                }

                // Only an isolated test fixture signals release. Production keeps ownership until the process exits.
                lease._release.Wait();
            }
            finally
            {
                if (lease is not null) Interlocked.CompareExchange(ref _current, null, lease);
                mutex.ReleaseMutex();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or
                                       WaitHandleCannotBeOpenedException or ArgumentException or InvalidOperationException or
                                       System.ComponentModel.Win32Exception)
        {
            Log.Error("Не удалось получить мьютекс журнала изменений", ex);
        }
        finally { ready.TrySetResult((null, false)); }
    }

    internal static bool TryEnter(Mutex mutex)
    {
        try { return mutex.WaitOne(TimeSpan.Zero); }
        catch (AbandonedMutexException) { return true; } // the previous owning thread has exited
    }

    internal static MutexSecurity CreateSecurity()
    {
        var security = new MutexSecurity();
        security.SetOwner(Administrators);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new MutexAccessRule(Administrators, MutexRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new MutexAccessRule(LocalSystem, MutexRights.FullControl, AccessControlType.Allow));
        return security;
    }

    internal static bool IsTrusted(MutexSecurity security)
    {
        const ControlFlags required = ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclProtected;
        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        return Trusted(descriptor.Owner) && (descriptor.ControlFlags & required) == required
            && descriptor.DiscretionaryAcl is { Count: > 0 } acl
            && acl.Cast<GenericAce>().All(ace =>
                ace is CommonAce { AceQualifier: AceQualifier.AccessAllowed } allow && Trusted(allow.SecurityIdentifier));
    }

    private static bool Trusted(SecurityIdentifier? sid) =>
        sid is not null && (Administrators.Equals(sid) || LocalSystem.Equals(sid));

    /// <summary>Ends an isolated test fixture after its writer operations have completed.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _release.Set();
        if (!_ownerThread.Join(HandshakeTimeout)) throw new TimeoutException();
        _release.Dispose();
    }
}
