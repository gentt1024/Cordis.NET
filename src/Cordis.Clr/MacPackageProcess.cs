using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Cordis.Clr;

// Spawn into a new group before any author code runs. Suspension keeps the leader
// available while its recovery record is written; ordinary descendants retain the group.
[SupportedOSPlatform("macos")]
internal sealed class MacPackageProcess : IDisposable
{
    private readonly Task<int> exit;
    internal Process Process
    {
        get;
    }
    internal StreamReader StandardOutput
    {
        get;
    }
    internal StreamReader StandardError
    {
        get;
    }
    internal int ExitCode => exit.GetAwaiter().GetResult();

    private MacPackageProcess(int pid, StreamReader output, StreamReader error)
    {
        Process = Process.GetProcessById(pid);
        StandardOutput = output;
        StandardError = error;
        exit = ReapAsync(pid);
    }

    internal static MacPackageProcess Start(ProcessStartInfo start)
    {
        using var arguments = new Strings([start.FileName, .. start.ArgumentList]);
        using var environment = new Strings(start.Environment.Select(pair => pair.Key + "=" + pair.Value));
        using var output = new Pipe();
        using var error = new Pipe();
        nint attributes = 0, actions = 0;
        var pid = 0;
        StreamReader? reader = null, errorReader = null;
        try
        {
            Check(Native.InitAttributes(out attributes));
            Check(Native.InitActions(out actions));
            Check(Native.SetGroup(ref attributes, 0));
            Check(Native.SetFlags(ref attributes, Native.SetProcessGroup | Native.StartSuspended |
                Native.CloseOnExecDefault | Native.SetSignalMask | Native.SetSignalDefaults));
            uint emptyMask = 0, defaults = uint.MaxValue;
            Check(Native.SetMask(ref attributes, ref emptyMask));
            Check(Native.SetDefaults(ref attributes, ref defaults));
            Check(Native.ChangeDirectory(ref actions, start.WorkingDirectory));
            Check(Native.Open(ref actions, 0, "/dev/null", 0, 0));
            Check(Native.Duplicate(ref actions, output.Write, 1));
            Check(Native.Duplicate(ref actions, error.Write, 2));
            Check(Native.Spawn(out var spawned, start.FileName, ref actions, ref attributes, arguments.Pointer, environment.Pointer));
            pid = spawned;
            reader = output.OpenReader();
            errorReader = error.OpenReader();
            return new(pid, reader, errorReader);
        }
        catch
        {
            if (pid > 0)
            {
                Native.Kill(-pid, 9);
                while (Native.WaitPid(pid, out _, 0) < 0 && Marshal.GetLastPInvokeError() == 4)
                {
                }
            }
            reader?.Dispose();
            errorReader?.Dispose();
            throw;
        }
        finally
        {
            if (actions != 0)
                Native.DestroyActions(ref actions);
            if (attributes != 0)
                Native.DestroyAttributes(ref attributes);
        }
    }

    internal void Resume()
    {
        if (Native.Kill(Process.Id, 19) != 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Resume package process");
    }

    internal Task WaitForExitAsync(CancellationToken token = default) => exit.WaitAsync(token);

    private static async Task<int> ReapAsync(int pid)
    {
        while (true)
        {
            var result = Native.WaitPid(pid, out var status, 1); // WNOHANG; this owner reaps its direct child.
            if (result == pid)
                return (status & 0x7f) == 0 ? (status >> 8) & 0xff : 128 + (status & 0x7f);
            if (result < 0 && Marshal.GetLastPInvokeError() != 4)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Wait for package process");
            await Task.Delay(20);
        }
    }

    internal static bool GroupAlive(int group)
    {
        if (Native.Kill(-group, 0) != 0 && Marshal.GetLastPInvokeError() == 3)
            return false;
        // Query group membership, including reparented descendants. A full buffer
        // is retried so an expanding group cannot be mistaken for an empty group.
        var capacity = 32;
        while (true)
        {
            var pids = new int[capacity];
            var bytes = Native.ListGroup(2, (uint)group, pids, pids.Length * sizeof(int));
            if (bytes <= 0)
                return Native.Kill(-group, 0) == 0 || Marshal.GetLastPInvokeError() != 3;
            if (bytes == pids.Length * sizeof(int))
            {
                capacity = checked(capacity * 2);
                continue;
            }
            foreach (var pid in pids.Take(bytes / sizeof(int)))
            {
                if (pid == 0)
                    continue;
                var info = new uint[16]; // proc_bsdshortinfo: pid, ppid, pgid, status, ... (64 bytes).
                var size = Native.ProcessInfo(pid, 13, 0, info, info.Length * sizeof(uint));
                if (size != info.Length * sizeof(uint))
                {
                    if (Native.Kill(pid, 0) != 0 && Marshal.GetLastPInvokeError() == 3)
                        continue;
                    return true;
                }
                if (info[2] == group && info[3] != 5) // SZOMB cannot execute or retain live threads.
                    return true;
            }
            return false;
        }
    }

    public void Dispose()
    {
        StandardOutput.Dispose();
        StandardError.Dispose();
        Process.Dispose();
    }

    private static void Check(int error)
    {
        if (error != 0)
            throw new Win32Exception(error, "Create package process");
    }

    private sealed class Pipe : IDisposable
    {
        private SafeFileHandle? read;
        private readonly SafeFileHandle write;
        internal int Write => (int)write.DangerousGetHandle();

        internal Pipe()
        {
            var descriptors = new int[2];
            if (Native.Pipe(descriptors) != 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            read = new((nint)descriptors[0], ownsHandle: true);
            write = new((nint)descriptors[1], ownsHandle: true);
            if (Native.CloseOnExec(descriptors[0], 0x20006601) < 0 || Native.CloseOnExec(descriptors[1], 0x20006601) < 0)
            {
                var error = Marshal.GetLastPInvokeError();
                Dispose();
                throw new Win32Exception(error);
            }
        }

        internal StreamReader OpenReader()
        {
            var reader = new StreamReader(new FileStream(read!, FileAccess.Read), Encoding.UTF8);
            read = null;
            return reader;
        }

        public void Dispose()
        {
            read?.Dispose();
            write.Dispose();
        }
    }

    private sealed class Strings : IDisposable
    {
        private readonly List<nint> strings = [];
        internal nint Pointer
        {
            get; private set;
        }

        internal Strings(IEnumerable<string> values)
        {
            try
            {
                foreach (var value in values)
                {
                    if (value.Contains('\0'))
                        throw new ArgumentException("Process values cannot contain NUL.");
                    strings.Add(Marshal.StringToCoTaskMemUTF8(value));
                }
                Pointer = Marshal.AllocHGlobal((strings.Count + 1) * IntPtr.Size);
                Marshal.Copy([.. strings, 0], 0, Pointer, strings.Count + 1);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            foreach (var value in strings)
                Marshal.FreeCoTaskMem(value);
            Marshal.FreeHGlobal(Pointer);
        }
    }

    private static class Native
    {
        internal const short SetProcessGroup = 0x0002, SetSignalDefaults = 0x0004, SetSignalMask = 0x0008,
            StartSuspended = 0x0080, CloseOnExecDefault = 0x4000;
        [DllImport("libc", EntryPoint = "posix_spawnattr_init")]
        internal static extern int InitAttributes(out nint attributes);
        [DllImport("libc", EntryPoint = "posix_spawnattr_destroy")]
        internal static extern int DestroyAttributes(ref nint attributes);
        [DllImport("libc", EntryPoint = "posix_spawn_file_actions_init")]
        internal static extern int InitActions(out nint actions);
        [DllImport("libc", EntryPoint = "posix_spawn_file_actions_destroy")]
        internal static extern int DestroyActions(ref nint actions);
        [DllImport("libc", EntryPoint = "posix_spawnattr_setpgroup")]
        internal static extern int SetGroup(ref nint attributes, int group);
        [DllImport("libc", EntryPoint = "posix_spawnattr_setflags")]
        internal static extern int SetFlags(ref nint attributes, short flags);
        [DllImport("libc", EntryPoint = "posix_spawnattr_setsigmask")]
        internal static extern int SetMask(ref nint attributes, ref uint mask);
        [DllImport("libc", EntryPoint = "posix_spawnattr_setsigdefault")]
        internal static extern int SetDefaults(ref nint attributes, ref uint mask);
        [DllImport("libc", EntryPoint = "posix_spawn_file_actions_addchdir_np")]
        internal static extern int ChangeDirectory(ref nint actions, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
        [DllImport("libc", EntryPoint = "posix_spawn_file_actions_addopen")]
        internal static extern int Open(ref nint actions, int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, ushort mode);
        [DllImport("libc", EntryPoint = "posix_spawn_file_actions_adddup2")]
        internal static extern int Duplicate(ref nint actions, int from, int to);
        [DllImport("libc", EntryPoint = "posix_spawnp")]
        internal static extern int Spawn(out int pid, [MarshalAs(UnmanagedType.LPUTF8Str)] string command, ref nint actions, ref nint attributes, nint arguments, nint environment);
        [DllImport("libc", EntryPoint = "pipe", SetLastError = true)]
        internal static extern int Pipe([Out] int[] descriptors);
        [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
        internal static extern int CloseOnExec(int fd, uint command);
        [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
        internal static extern int Kill(int pid, int signal);
        [DllImport("libc", EntryPoint = "waitpid", SetLastError = true)]
        internal static extern int WaitPid(int pid, out int status, int options);
        [DllImport("libproc", EntryPoint = "proc_listpids", SetLastError = true)]
        internal static extern int ListGroup(uint type, uint group, [Out] int[] pids, int size);
        [DllImport("libproc", EntryPoint = "proc_pidinfo", SetLastError = true)]
        internal static extern int ProcessInfo(int pid, int flavor, ulong argument, [Out] uint[] info, int size);
    }
}
