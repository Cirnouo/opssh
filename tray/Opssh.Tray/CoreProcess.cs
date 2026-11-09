using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Opssh.Tray;

internal sealed class CoreProcess : IDisposable
{
    private readonly SafeWaitHandle process;
    internal NamedPipeServerStream Pipe { get; }

    private CoreProcess(NamedPipeServerStream pipe, SafeWaitHandle process)
    {
        Pipe = pipe;
        this.process = process;
    }

    internal static async Task<CoreProcess> StartAsync(CancellationToken token)
    {
        var exe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "opssh-core.exe"));
        if (!File.Exists(exe))
            throw new FileNotFoundException("The bundled opssh core executable is missing.");

        var name = @"\\.\pipe\opssh-control-" + Guid.NewGuid().ToString("N");
        var server = CreatePrivateServer(name);
        try
        {
            // Leave the raw client handle unbound to a completion port so the
            // receiving process can attach its own overlapped I/O implementation.
            using var client = Native.CreateFileW(name, 0xc0000000, 0, 0, 3, 0x40110000, 0);
            if (client.IsInvalid)
                throw Failure("Could not preconnect the control pipe.");
            await server.WaitForConnectionAsync(token).ConfigureAwait(false);
            if (!Native.GetNamedPipeClientProcessId(server.SafePipeHandle, out var clientPid) ||
                !Native.GetNamedPipeServerProcessId(client, out var serverPid) ||
                clientPid != Environment.ProcessId || serverPid != Environment.ProcessId)
                throw new IOException("Control pipe bootstrap did not connect to this tray.");

            token.ThrowIfCancellationRequested();
            using var childPipe = Duplicate(client.DangerousGetHandle(), 0, sameAccess: true);
            using var trayProcess = Duplicate(new nint(-1), 0x00100000 | 0x1000, sameAccess: false);
            var child = Launch(exe, childPipe, trayProcess);
            return new CoreProcess(server, child);
        }
        catch
        {
            server.Dispose();
            throw;
        }
    }

    private static NamedPipeServerStream CreatePrivateServer(string name)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new IOException("Could not identify the tray user.");
        if (!Native.ConvertStringSecurityDescriptorToSecurityDescriptorW($"D:P(A;;GA;;;{sid})", 1, out var descriptor, 0))
            throw Failure("Could not set the private pipe permissions.");
        try
        {
            var attributes = new Native.SecurityAttributes
            {
                Size = Marshal.SizeOf<Native.SecurityAttributes>(),
                Descriptor = descriptor
            };
            const uint duplexOverlappedFirstInstance = 0x40080003;
            const uint rejectRemoteClients = 8;
            const uint pipeBufferSize = 64 * 1024;
            var handle = Native.CreateNamedPipeW(name, duplexOverlappedFirstInstance, rejectRemoteClients, 1,
                pipeBufferSize, pipeBufferSize, 0, ref attributes);
            if (handle.IsInvalid)
            {
                var error = Failure("Could not create the private control pipe.");
                handle.Dispose();
                throw error;
            }
            try
            {
                return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, handle);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        finally
        {
            Native.LocalFree(descriptor);
        }
    }

    private static SafeWaitHandle Duplicate(nint original, uint access, bool sameAccess)
    {
        var current = new nint(-1); // GetCurrentProcess pseudo handle.
        if (!Native.DuplicateHandle(current, original, current, out var result, access, true, sameAccess ? 2u : 0u))
        {
            result.Dispose();
            throw Failure("Could not prepare inherited control handles.");
        }
        return result;
    }

    private static SafeWaitHandle Launch(string exe, SafeWaitHandle pipe, SafeWaitHandle tray)
    {
        const uint extendedStartupInfo = 0x00080000;
        const uint createNoWindow = 0x08000000;
        const int handleList = 0x00020002;
        nuint size = 0;
        Native.InitializeProcThreadAttributeList(0, 1, 0, ref size);
        if (size == 0)
            throw Failure("Could not size the inherited handle list.");
        var attributes = Marshal.AllocHGlobal(checked((nint)size));
        var handles = nint.Zero;
        var initialized = false;
        try
        {
            if (!Native.InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
                throw Failure("Could not initialize the inherited handle list.");
            initialized = true;

            handles = Marshal.AllocHGlobal(2 * nint.Size);
            var pipeHandle = pipe.DangerousGetHandle();
            var trayHandle = tray.DangerousGetHandle();
            Marshal.WriteIntPtr(handles, 0, pipeHandle);
            Marshal.WriteIntPtr(handles, nint.Size, trayHandle);
            if (!Native.UpdateProcThreadAttribute(attributes, 0, handleList, handles, (nuint)(2 * nint.Size), 0, 0))
                throw Failure("Could not restrict inherited handles.");

            var startup = new Native.StartupInfoEx
            {
                Startup = new Native.StartupInfo { Size = Marshal.SizeOf<Native.StartupInfoEx>() },
                Attributes = attributes
            };
            var command = new StringBuilder(string.Create(CultureInfo.InvariantCulture,
                $"\"{exe}\" --control-handle {(nuint)pipeHandle} --tray-handle {(nuint)trayHandle}"));
            if (!Native.CreateProcessW(exe, command, 0, 0, true, extendedStartupInfo | createNoWindow,
                    0, Path.GetDirectoryName(exe)!, ref startup, out var info))
                throw Failure("Could not start the bundled opssh core.");

            using var thread = new SafeWaitHandle(info.Thread, ownsHandle: true);
            return new SafeWaitHandle(info.Process, ownsHandle: true);
        }
        finally
        {
            if (initialized)
                Native.DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(handles);
            Marshal.FreeHGlobal(attributes);
        }
    }

    internal async Task<bool> WaitAsync(TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            var status = Native.WaitForSingleObject(process, 0);
            if (status == 0)
                return true;
            if (status != 258)
                throw Failure("Could not observe the owned core process.");
            var remaining = timeout - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero)
                return false;
            await Task.Delay(remaining < TimeSpan.FromMilliseconds(50) ? remaining : TimeSpan.FromMilliseconds(50))
                .ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        Pipe.Dispose();
        if (!process.IsClosed)
        {
            if (Native.WaitForSingleObject(process, 0) == 258)
            {
                // This handle came directly from CreateProcessW. A PID lookup could target a reused PID.
                Native.TerminateProcess(process, 1);
                Native.WaitForSingleObject(process, 1000);
            }
            process.Dispose();
        }
    }

    private static Win32Exception Failure(string text) => new(Marshal.GetLastWin32Error(), text);

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct StartupInfo
        {
            internal int Size;
            internal nint Reserved, Desktop, Title;
            internal uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
            internal ushort ShowWindow, ReservedSize;
            internal nint ReservedBytes, StdInput, StdOutput, StdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct StartupInfoEx
        {
            internal StartupInfo Startup;
            internal nint Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ProcessInformation
        {
            internal nint Process, Thread;
            internal uint ProcessId, ThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SecurityAttributes
        {
            internal int Size;
            internal nint Descriptor;
            internal int Inherit;
        }

        [DllImport("advapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision,
            out nint descriptor, nint size);

        [DllImport("kernel32.dll")]
        internal static extern nint LocalFree(nint memory);

        [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafePipeHandle CreateNamedPipeW(string name, uint openMode, uint pipeMode, uint maxInstances,
            uint outputBuffer, uint inputBuffer, uint timeout, ref SecurityAttributes attributes);

        [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafePipeHandle CreateFileW(string name, uint access, uint share, nint attributes,
            uint creationDisposition, uint flags, nint template);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool DuplicateHandle(nint source, nint handle, nint target, out SafeWaitHandle duplicate,
            uint access, bool inherit, uint options);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool InitializeProcThreadAttributeList(nint attributes, int count, uint flags, ref nuint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool UpdateProcThreadAttribute(nint attributes, uint flags, nint attribute,
            nint value, nuint size, nint previous, nint returnedSize);

        [DllImport("kernel32.dll")]
        internal static extern void DeleteProcThreadAttributeList(nint attributes);

        [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool CreateProcessW(string application, StringBuilder command, nint processAttributes,
            nint threadAttributes, bool inheritHandles, uint flags, nint environment, string directory,
            ref StartupInfoEx startup, out ProcessInformation result);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool TerminateProcess(SafeWaitHandle process, uint exitCode);
    }
}
