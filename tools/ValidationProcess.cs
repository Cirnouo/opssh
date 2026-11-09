using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Opssh.Verification;

public sealed record ValidationProcessResult(int ExitCode, bool TimedOut, bool CleanedUp, bool Canceled = false);

public static class ValidationProcess
{
    private const int ExtendedLimitInformation = 9;
    private const int BasicAccountingInformation = 1;
    private const uint KillOnJobClose = 0x00002000;

    public static ValidationProcessResult Run(string shell, string shim, string driver,
        string output, string report, int timeoutMilliseconds, int ownerId, long ownerStartedUtcTicks)
    {
        var clock = Stopwatch.StartNew();
        using var owner = ownerId == 0 ? null : Process.GetProcessById(ownerId);
        if (owner is not null)
        {
            _ = owner.Handle;
            if (owner.HasExited || owner.StartTime.ToUniversalTime().Ticks != ownerStartedUtcTicks)
                throw new InvalidOperationException("The Git validation owner is no longer active.");
        }
        using var job = CreateJobObject(IntPtr.Zero, null);
        var limits = new JobLimits { Basic = new BasicLimits { LimitFlags = KillOnJobClose } };
        if (job.IsInvalid ||
            !SetInformationJobObject(job, ExtendedLimitInformation, ref limits, Marshal.SizeOf<JobLimits>()))
            throw new InvalidOperationException("Cannot initialize the Windows validation job.");

        var start = new ProcessStartInfo(shell)
        {
            WorkingDirectory = Path.GetDirectoryName(driver),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] {
            "-NoLogo", "-NoProfile", "-NonInteractive", "-File", shim, driver, output, report
        })
            start.ArgumentList.Add(argument);

        foreach (var name in start.Environment.Keys.Where(name =>
            name.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(name);
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        start.Environment["UseSharedCompilation"] = "false";

        using var process = Process.Start(start) ??
            throw new InvalidOperationException("Cannot start the Windows validation driver.");
        try
        {
            if (!AssignProcessToJobObject(job, process.Handle))
                throw new InvalidOperationException("Cannot contain the Windows validation driver.");
            var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            process.StandardInput.WriteLine("run");
            process.StandardInput.Close();

            while (clock.ElapsedMilliseconds < timeoutMilliseconds && owner?.HasExited != true)
            {
                if (process.HasExited && stdout.IsCompleted && stderr.IsCompleted && ActiveProcesses(job) == 0)
                {
                    if (!stdout.IsCompletedSuccessfully || !stderr.IsCompletedSuccessfully)
                        return new ValidationProcessResult(-1, false, true);
                    return new ValidationProcessResult(process.ExitCode, false, true);
                }
                Thread.Sleep(25);
            }

            var canceled = owner?.HasExited == true;
            TerminateJobObject(job, 1);
            var cleanup = Stopwatch.StartNew();
            while (cleanup.ElapsedMilliseconds < 5000)
            {
                if (process.HasExited && stdout.IsCompleted && stderr.IsCompleted && ActiveProcesses(job) == 0)
                    return new ValidationProcessResult(-1, !canceled, true, canceled);
                Thread.Sleep(25);
            }
            return new ValidationProcessResult(-1, !canceled, false, canceled);
        }
        finally
        {
            // Closing the job reaps owned descendants even when an exception interrupts the wait.
            // Before assignment, a launcher whose stdin closes can only exit without starting the driver.
            process.StandardInput.Close();
        }
    }

    private static uint ActiveProcesses(SafeFileHandle job)
    {
        if (!QueryInformationJobObject(job, BasicAccountingInformation,
            out var accounting, Marshal.SizeOf<JobAccounting>(), IntPtr.Zero))
            throw new InvalidOperationException("Cannot read the Windows validation job status.");
        return accounting.ActiveProcesses;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobLimits
    {
        public BasicLimits Basic;
        public ulong ReadOperations, WriteOperations, OtherOperations;
        public ulong ReadBytes, WriteBytes, OtherBytes;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobAccounting
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodUserTime, ThisPeriodKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string name);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass,
        ref JobLimits limits, int size);

    [DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

    [DllImport("kernel32.dll")]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int informationClass,
        out JobAccounting accounting, int size, IntPtr returnedSize);

    [DllImport("kernel32.dll")]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
}
