using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.CSharp.RuntimeBinder;
using Microsoft.Win32.SafeHandles;

namespace Opssh.Tray;

internal static class OnePasswordReadiness
{
    private static readonly SemaphoreSlim WmiSlot = new(1);
    private static readonly SemaphoreSlim ProbeSlot = new(1);

    internal static Task WaitAsync(CancellationToken token) =>
        Task.Run(async () =>
        {
            using var current = Process.GetCurrentProcess();
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            var user = identity.User;
            if (user is null)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                return;
            }
            var session = (uint)current.SessionId;
            await ObserveAsync(() => IsRunning(session, user), (check, stop) => WatchAsync(session, check, stop),
                stop => Task.Delay(TimeSpan.FromSeconds(5), stop), token).ConfigureAwait(false);
        }, token).WaitAsync(token);

    internal static async Task ObserveAsync(Func<bool> check, Func<Func<bool>, CancellationToken, Task<bool>> watch,
        Func<CancellationToken, Task> delay, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (check())
        {
            token.ThrowIfCancellationRequested();
            return;
        }

        var signaled = await watch(check, token).ConfigureAwait(false);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (signaled || check())
            {
                token.ThrowIfCancellationRequested();
                return;
            }
            await delay(token).ConfigureAwait(false);
        }
    }

    private static async Task<bool> WatchAsync(uint session, Func<bool> check, CancellationToken token)
    {
        if (!WmiSlot.Wait(0))
            return false;

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var stopped = stop.Token;
        var subscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = Task.Factory.StartNew(() =>
        {
            try { return Watch(session, check, subscribed, stopped); }
            finally { WmiSlot.Release(); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            var ready = await Task.WhenAny(watcher, subscribed.Task)
                .WaitAsync(TimeSpan.FromSeconds(4), token).ConfigureAwait(false);
            if (ready == watcher)
                return await watcher.ConfigureAwait(false);
            return await watcher.WaitAsync(token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return false;
        }
        finally
        {
            stop.Cancel();
        }
    }

    private static bool Watch(uint session, Func<bool> check, TaskCompletionSource subscribed, CancellationToken token)
    {
        object? locator = null, service = null, events = null;
        try
        {
            var type = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (type is null || token.IsCancellationRequested)
                return false;
            locator = Activator.CreateInstance(type);
            if (locator is null)
                return false;
            service = ((dynamic)locator).ConnectServer(".", @"root\cimv2", "", "", "", "", 128);
            if (token.IsCancellationRequested)
                return false;
            var query = string.Create(CultureInfo.InvariantCulture,
                $"SELECT * FROM Win32_ProcessStartTrace WHERE ProcessName = '1Password.exe' AND SessionID = {session}");
            events = ((dynamic)service).ExecNotificationQuery(query, "WQL", 0x30);
            subscribed.TrySetResult();
            // Close the gap between the initial scan and event registration.
            if (!token.IsCancellationRequested && check())
                return true;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    object? notification = ((dynamic)events).NextEvent(5000);
                    Release(notification);
                    if (!token.IsCancellationRequested && check())
                        return true;
                }
                catch (COMException e) when (e.HResult == unchecked((int)0x80043001))
                {
                    // The bounded wait only lets this thread observe cancellation.
                }
            }
        }
        catch (Exception e) when (e is COMException or RuntimeBinderException or UnauthorizedAccessException or PlatformNotSupportedException) { }
        finally
        {
            Release(events);
            Release(service);
            Release(locator);
        }
        return false;
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.ReleaseComObject(value);
    }

    private static bool IsRunning(uint session, SecurityIdentifier user)
    {
        if (!ProbeSlot.Wait(0))
            return false;
        try
        {
            var processes = Process.GetProcessesByName("1Password");
            try
            {
                return processes.Take(32).Any(process => Matches((uint)process.Id, session, user));
            }
            finally
            {
                foreach (var process in processes)
                    process.Dispose();
            }
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            return false;
        }
        finally
        {
            ProbeSlot.Release();
        }
    }

    private static bool Matches(uint id, uint session, SecurityIdentifier user)
    {
        const uint queryAndSynchronize = 0x1000 | 0x00100000;
        using var process = OpenProcess(queryAndSynchronize, false, id);
        if (process.IsInvalid || !ProcessIdToSessionId(id, out var candidateSession) || candidateSession != session)
            return false;
        if (!OpenProcessToken(process, 8, out var token))
        {
            token.Dispose();
            return false;
        }
        using (token)
        {
            try
            {
                using var identity = new WindowsIdentity(token.DangerousGetHandle());
                if (identity.User is not { } owner || !user.Equals(owner))
                    return false;
                var path = new StringBuilder(32768);
                var length = path.Capacity;
                if (!QueryFullProcessImageNameW(process, 0, path, ref length))
                    return false;
                var image = path.ToString();
                if (!string.Equals(Path.GetFileName(image), "1Password.exe", StringComparison.OrdinalIgnoreCase))
                    return false;
                using var file = File.OpenHandle(image, FileMode.Open, FileAccess.Read, FileShare.Read);
                return OnePasswordSignature.Verify(image, file) && WaitForSingleObject(process, 0) == 258;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException or Win32Exception)
            {
                return false;
            }
        }
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder path, ref int size);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
}
