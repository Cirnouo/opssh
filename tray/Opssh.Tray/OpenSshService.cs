using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Opssh.Tray;

internal sealed record OpenSshState(bool Exists, int Start, bool Delayed, bool Running, uint Pid)
{
    internal bool Automatic => Start == 2;
}

internal interface IOpenSshService
{
    OpenSshState Read();
    bool PipeAvailable(uint expectedOwner = 0);
    Task ChangeStartupAsync(int start, bool delayed);
    Task ChangeRunningAsync(bool running);
}

internal sealed class OpenSshService : IOpenSshService
{
    internal const string Pipe = @"\\.\pipe\openssh-ssh-agent";
    private const string Name = "ssh-agent";
    private static string Image => Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh-agent.exe");

    public OpenSshState Read()
    {
        using var manager = Native.OpenSCManagerW(null, null, 1);
        if (manager.IsInvalid)
            throw NativeError();
        using var service = Native.OpenServiceW(manager, Name, 5);
        if (service.IsInvalid)
        {
            if (Marshal.GetLastWin32Error() == 1060)
                return new(false, 0, false, false, 0);
            throw NativeError();
        }

        Native.QueryServiceConfigW(service, 0, 0, out var size);
        if (size is < 1 or > 8192)
            throw NativeError();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!Native.QueryServiceConfigW(service, buffer, size, out _))
                throw NativeError();
            var config = Marshal.PtrToStructure<Native.Config>(buffer);
            var image = Marshal.PtrToStringUni(config.Image)?.Trim().Trim('"');
            if (image is null || !string.Equals(Environment.ExpandEnvironmentVariables(image), Image, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The service is not the inbox Windows OpenSSH agent.");
            if (!Native.QueryServiceConfig2W(service, 3, out var delayed, sizeof(int), out _))
                throw NativeError();
            if (!Native.QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<Native.Status>(), out _))
                throw NativeError();
            if (status.Current is not (1 or 4))
                throw new ServiceChangePending();
            return new(true, config.Start, delayed != 0, status.Current == 4, status.Pid);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    // An inaccessible or saturated pipe is treated as occupied. The actual
    // first-instance bind by the core is the final ownership check.
    public bool PipeAvailable(uint expectedOwner = 0)
    {
        using var pipe = Native.CreateFileW(Pipe, 0, 0, 0, 3, 0x00110000, 0);
        if (pipe.IsInvalid)
            return expectedOwner == 0 && Marshal.GetLastWin32Error() == 2;
        return expectedOwner != 0 && Native.GetNamedPipeServerProcessId(pipe, out var pid) && pid == expectedOwner;
    }

    public async Task ChangeStartupAsync(int start, bool delayed)
    {
        var mode = start switch
        {
            2 when delayed => "delayed-auto",
            2 => "auto",
            3 => "demand",
            4 => "disabled",
            _ => throw new IOException("Unexpected OpenSSH startup mode.")
        };
        await RunAsync("config", Name, "start=", mode).ConfigureAwait(false);
    }

    public async Task ChangeRunningAsync(bool running)
    {
        await RunAsync(running ? "start" : "stop", Name).ConfigureAwait(false);
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromMinutes(2))
        {
            try
            {
                if (Read().Running == running)
                    return;
            }
            catch (ServiceChangePending) { }
            await Task.Delay(200).ConfigureAwait(false);
        }
        throw new ServiceChangePending();
    }

    private static async Task RunAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"))
        {
            Verb = "runas",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Environment.SystemDirectory
        };
        foreach (var value in arguments)
            start.ArgumentList.Add(value);
        using var process = await Task.Run(() => Process.Start(start)).ConfigureAwait(false)
            ?? throw new IOException("Windows could not launch Service Control.");
        await process.WaitForExitAsync().ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new IOException("Windows rejected the OpenSSH service change.");
    }

    private static Win32Exception NativeError() => new(Marshal.GetLastWin32Error(), "Unable to inspect the Windows OpenSSH service.");

    internal sealed class ServiceChangePending : IOException { }

    private sealed class ServiceHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle() => Native.CloseServiceHandle(handle);
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Config
        {
            internal int Type, Start, Error;
            internal nint Image, Group;
            internal int Tag;
            internal nint Dependencies, Account, DisplayName;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Status
        {
            internal int Type, Current, Controls, Win32Exit, ServiceExit, Checkpoint, WaitHint;
            internal uint Pid, Flags;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        internal static extern ServiceHandle OpenSCManagerW(string? computer, string? database, uint access);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        internal static extern ServiceHandle OpenServiceW(ServiceHandle manager, string name, uint access);
        [DllImport("advapi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseServiceHandle(nint handle);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryServiceConfigW(ServiceHandle service, nint buffer, int bytes, out int required);
        [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryServiceConfig2W(ServiceHandle service, int level, out int value, int bytes, out int required);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryServiceStatusEx(ServiceHandle service, int level, out Status status, int bytes, out int required);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        internal static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint attributes, uint disposition, uint flags, nint template);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetNamedPipeServerProcessId(SafeFileHandle pipe, out uint pid);
    }
}
