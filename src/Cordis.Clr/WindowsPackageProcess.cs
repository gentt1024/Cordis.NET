using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Cordis.Clr;

// Ordinary descendants inherit this job even if they close or redirect their output.
// Job-list creation avoids both the Start/Assign race and an orphaned suspended process
// when the host dies. This owns a process lifetime; it is not a build-code sandbox.
[SupportedOSPlatform("windows")]
internal sealed class WindowsPackageProcess : IDisposable
{
    private readonly SafeFileHandle job;
    private readonly SafeFileHandle initialThread;
    internal string JobName { get; }
    internal Process Process { get; }
    internal StreamReader StandardOutput { get; }
    internal StreamReader StandardError { get; }

    private WindowsPackageProcess(string name, SafeFileHandle job, SafeFileHandle thread,
        Process process, StreamReader output, StreamReader error)
    {
        JobName = name;
        this.job = job;
        initialThread = thread;
        Process = process;
        StandardOutput = output;
        StandardError = error;
    }

    internal static WindowsPackageProcess Start(ProcessStartInfo start)
    {
        var name = "Local\\Cordis.Package." + Guid.NewGuid().ToString("N");
        var job = Native.CreateJobObject(0, name);
        if (job.IsInvalid) throw Failure("Create package job");
        SafeFileHandle? thread = null;
        Process? process = null;
        StreamReader? output = null;
        StreamReader? error = null;
        try
        {
            var limits = new Native.ExtendedLimits { Basic = new() { Flags = Native.KillOnJobClose } };
            if (!Native.SetJobLimits(job, Native.ExtendedLimitInformation, ref limits, Marshal.SizeOf<Native.ExtendedLimits>()))
                throw Failure("Set package job lifetime");
            var security = new Native.SecurityAttributes { Size = Marshal.SizeOf<Native.SecurityAttributes>(), Inherit = true };
            if (!Native.CreatePipe(out var outputRead, out var outputWrite, ref security, 0)) throw Failure("Create output pipe");
            using (outputWrite)
            using (outputRead)
            {
                if (!Native.CreatePipe(out var errorRead, out var errorWrite, ref security, 0)) throw Failure("Create error pipe");
                using (errorWrite)
                using (errorRead)
                {
                    if (!Native.CreatePipe(out var inputRead, out var inputWrite, ref security, 0)) throw Failure("Create input pipe");
                    using (inputRead)
                    using (inputWrite)
                    {
                        // Inherit exactly the three child standard handles. The job handle is
                        // never inherited, so host death closes its last owning handle.
                        if (!Native.SetHandleInformation(outputRead, 1, 0) || !Native.SetHandleInformation(errorRead, 1, 0)
                            || !Native.SetHandleInformation(inputWrite, 1, 0)) throw Failure("Limit pipe inheritance");
                        using var attributes = new ProcessAttributes(job, inputRead, outputWrite, errorWrite);
                        var startup = new Native.StartupInfoEx
                        {
                            Info = new()
                            {
                                Size = Marshal.SizeOf<Native.StartupInfoEx>(), Flags = Native.UseStandardHandles,
                                Input = inputRead.DangerousGetHandle(), Output = outputWrite.DangerousGetHandle(), Error = errorWrite.DangerousGetHandle(),
                            },
                            Attributes = attributes.Pointer,
                        };
                        var command = new StringBuilder(string.Join(' ', new[] { start.FileName }.Concat(start.ArgumentList).Select(Quote)));
                        var environment = string.Join('\0', start.Environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                            .Where(pair => pair.Value is not null).Select(pair => pair.Key + "=" + pair.Value)) + "\0\0";
                        var environmentPointer = Marshal.StringToHGlobalUni(environment);
                        try
                        {
                            // Membership is assigned by CreateProcess itself. Suspension is only
                            // for recording the process/job identity before package code executes.
                            var flags = Native.CreateSuspended | Native.CreateUnicodeEnvironment | Native.ExtendedStartupInfo | Native.CreateNoWindow;
                            if (!Native.CreateProcess(null, command, 0, 0, true, flags, environmentPointer,
                                start.WorkingDirectory, ref startup, out var created)) throw Failure("Start package process");
                            using var nativeProcess = new SafeFileHandle(created.Process, true);
                            thread = new(created.Thread, true);
                            process = Process.GetProcessById(created.Id);
                            _ = process.SafeHandle;
                            _ = process.StartTime;
                        }
                        finally { Marshal.FreeHGlobal(environmentPointer); }
                    }
                    // StreamReader owns non-inheritable duplicates; these local pipe
                    // handles close when startup returns.
                    output = Reader(outputRead, start.StandardOutputEncoding ?? Encoding.UTF8);
                    error = Reader(errorRead, start.StandardErrorEncoding ?? Encoding.UTF8);
                }
            }
            return new(name, job, thread!, process!, output, error);
        }
        catch
        {
            job.Dispose();
            thread?.Dispose();
            process?.Dispose();
            output?.Dispose();
            error?.Dispose();
            throw;
        }
    }

    internal void Resume()
    {
        if (Native.ResumeThread(initialThread) == uint.MaxValue) throw Failure("Resume package process");
        initialThread.Dispose();
    }

    internal void Stop()
    {
        if (!Native.TerminateJob(job, 1)) throw Failure("Terminate package processes");
    }

    internal async Task<bool> WaitUntilEmptyAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var elapsed = Stopwatch.StartNew();
        while (ActiveProcesses(job) != 0)
        {
            if (elapsed.Elapsed >= timeout) return false;
            await Task.Delay(20, cancellationToken);
        }
        return true;
    }

    internal static bool IsAlive(string name)
    {
        if (!name.StartsWith("Local\\Cordis.Package.", StringComparison.Ordinal)
            || !Guid.TryParseExact(name["Local\\Cordis.Package.".Length..], "N", out _))
            throw new IOException("The prior package job identity is invalid; its run record remains for recovery.");
        using var job = Native.OpenJobObject(Native.JobQuery, false, name);
        if (job.IsInvalid)
        {
            if (Marshal.GetLastPInvokeError() == 2) return false;
            throw Failure("Inspect prior package job");
        }
        return ActiveProcesses(job) != 0;
    }

    private static uint ActiveProcesses(SafeFileHandle job)
    {
        if (!Native.QueryJobAccounting(job, Native.BasicAccountingInformation, out var information, Marshal.SizeOf<Native.Accounting>(), 0))
            throw Failure("Inspect package processes");
        return information.ActiveProcesses;
    }

    private static StreamReader Reader(SafeFileHandle original, Encoding encoding)
    {
        if (!Native.DuplicateHandle(Native.GetCurrentProcess(), original, Native.GetCurrentProcess(), out var duplicate, 0, false, 2))
            throw Failure("Own package output pipe");
        return new(new FileStream(duplicate, FileAccess.Read, 4096, isAsync: false), encoding, detectEncodingFromByteOrderMarks: false);
    }

    private static string Quote(string value)
    {
        if (value.Length != 0 && !value.Any(character => char.IsWhiteSpace(character) || character == '"')) return value;
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            result.Append(character);
            slashes = 0;
        }
        result.Append('\\', slashes * 2);
        return result.Append('"').ToString();
    }

    private static IOException Failure(string action) => new(action + " failed.", new Win32Exception(Marshal.GetLastPInvokeError()));

    public void Dispose()
    {
        job.Dispose();
        initialThread.Dispose();
        StandardOutput.Dispose();
        StandardError.Dispose();
        Process.Dispose();
    }

    private sealed class ProcessAttributes : IDisposable
    {
        internal nint Pointer { get; }
        private readonly nint handles;
        private readonly nint jobs;
        private bool initialized;
        internal ProcessAttributes(SafeFileHandle job, params SafeFileHandle[] inherited)
        {
            nuint size = 0;
            Native.InitializeAttributes(0, 2, 0, ref size);
            Pointer = Marshal.AllocHGlobal(checked((nint)size));
            handles = Marshal.AllocHGlobal(inherited.Length * nint.Size);
            jobs = Marshal.AllocHGlobal(nint.Size);
            try
            {
                if (!Native.InitializeAttributes(Pointer, 2, 0, ref size)) throw Failure("Initialize package startup attributes");
                initialized = true;
                for (var index = 0; index < inherited.Length; index++) Marshal.WriteIntPtr(handles, index * nint.Size, inherited[index].DangerousGetHandle());
                Marshal.WriteIntPtr(jobs, job.DangerousGetHandle());
                if (!Native.UpdateAttribute(Pointer, 0, Native.HandleListAttribute, handles, (nuint)(inherited.Length * nint.Size), 0, 0)
                    || !Native.UpdateAttribute(Pointer, 0, Native.JobListAttribute, jobs, (nuint)nint.Size, 0, 0)) throw Failure("Bind package handles and job");
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            if (initialized) Native.DeleteAttributes(Pointer);
            Marshal.FreeHGlobal(Pointer);
            Marshal.FreeHGlobal(handles);
            Marshal.FreeHGlobal(jobs);
        }
    }

    private static class Native
    {
        internal const uint KillOnJobClose = 0x2000, JobQuery = 4;
        internal const int BasicAccountingInformation = 1, ExtendedLimitInformation = 9, UseStandardHandles = 0x100;
        internal const uint CreateSuspended = 4, CreateUnicodeEnvironment = 0x400, ExtendedStartupInfo = 0x80000, CreateNoWindow = 0x08000000;
        internal const uint HandleListAttribute = 0x20002, JobListAttribute = 0x2000d;
        [StructLayout(LayoutKind.Sequential)]
        internal struct SecurityAttributes { internal int Size; internal nint Descriptor; [MarshalAs(UnmanagedType.Bool)] internal bool Inherit; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct StartupInfo
        {
            internal int Size;
            internal nint Reserved, Desktop, Title;
            internal int X, Y, XSize, YSize, XCount, YCount, Fill, Flags;
            internal short Show, ReservedSize;
            internal nint ReservedBytes, Input, Output, Error;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct StartupInfoEx { internal StartupInfo Info; internal nint Attributes; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct ProcessInfo { internal nint Process, Thread; internal int Id, ThreadId; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct BasicLimits
        {
            internal long ProcessTime, JobTime;
            internal uint Flags;
            internal nuint MinimumWorkingSet, MaximumWorkingSet;
            internal uint ActiveProcessLimit;
            internal nuint Affinity;
            internal uint Priority, Scheduling;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct IoCounters { internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct ExtendedLimits
        {
            internal BasicLimits Basic;
            internal IoCounters Io;
            internal nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Accounting
        {
            internal long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime;
            internal uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateJobObject(nint security, string name);
        [DllImport("kernel32.dll", EntryPoint = "OpenJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle OpenJobObject(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);
        [DllImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetJobLimits(SafeFileHandle job, int kind, ref ExtendedLimits limits, int size);
        [DllImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryJobAccounting(SafeFileHandle job, int kind, out Accounting accounting, int size, nint returned);
        [DllImport("kernel32.dll", EntryPoint = "TerminateJobObject", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TerminateJob(SafeFileHandle job, uint code);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes security, int size);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DuplicateHandle(nint process, SafeFileHandle original, nint target, out SafeFileHandle duplicate, uint access,
            [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
        [DllImport("kernel32.dll")]
        internal static extern nint GetCurrentProcess();
        [DllImport("kernel32.dll", EntryPoint = "InitializeProcThreadAttributeList", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool InitializeAttributes(nint list, int count, uint flags, ref nuint size);
        [DllImport("kernel32.dll", EntryPoint = "UpdateProcThreadAttribute", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UpdateAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returned);
        [DllImport("kernel32.dll", EntryPoint = "DeleteProcThreadAttributeList")]
        internal static extern void DeleteAttributes(nint list);
        [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateProcess(string? application, StringBuilder command, nint processSecurity, nint threadSecurity,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, nint environment, string directory, ref StartupInfoEx startup, out ProcessInfo process);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint ResumeThread(SafeFileHandle thread);
    }
}
