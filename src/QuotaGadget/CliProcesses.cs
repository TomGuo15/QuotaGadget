using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace QuotaGadget;

internal static partial class CliProcesses
{
    internal static bool IsAlive(int pid)
    {
        const uint synchronize = 0x100000, waitTimeout = 258;
        nint process = OpenProcess(synchronize, 0, (uint)pid);
        if (process == 0) return false;
        try { return WaitForSingleObject(process, 0) == waitTimeout; }
        finally { CloseHandle(process); }
    }

    internal static unsafe bool IsInUse(Provider provider)
    {
        const uint allProcesses = 2;
        using var snapshot = new SafeFileHandle(CreateToolhelp32Snapshot(allProcesses, 0), ownsHandle: true);
        if (snapshot.IsInvalid) return true;

        var entry = new ProcessEntry { Size = (uint)sizeof(ProcessEntry) };
        for (int present = Process32FirstW(snapshot, ref entry); present != 0; present = Process32NextW(snapshot, ref entry))
        {
            ReadOnlySpan<char> name = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(entry.Name);
            if (MatchesCli(name, provider) ||
                (provider == Provider.Codex && name.Equals("node.exe", StringComparison.OrdinalIgnoreCase) && IsCodexNode(entry.Id)))
                return true;
        }
        return Marshal.GetLastPInvokeError() != 18; // ERROR_NO_MORE_FILES
    }

    private static bool MatchesCli(ReadOnlySpan<char> name, Provider provider) => provider switch
    {
        Provider.Codex => name.Equals("codex.exe", StringComparison.OrdinalIgnoreCase) ||
            (name.StartsWith("codex-", StringComparison.OrdinalIgnoreCase) && name.EndsWith("-pc-windows-msvc.exe", StringComparison.OrdinalIgnoreCase)),
        Provider.Claude => name.Equals("claude.exe", StringComparison.OrdinalIgnoreCase),
        _ => name.Equals("agy.exe", StringComparison.OrdinalIgnoreCase)
    };

    private static unsafe bool IsCodexNode(uint pid)
    {
        const uint queryLimitedInformation = 0x1000;
        nint process = OpenProcess(queryLimitedInformation, 0, pid);
        if (process == 0) return NodeMayBeCodexAfterOpenFailure(Marshal.GetLastPInvokeError());
        try
        {
            const int commandLineInformation = 60;
            const int stackBytes = 2048;
            byte* data = stackalloc byte[stackBytes];
            int status = NtQueryInformationProcess(process, commandLineInformation, data, stackBytes, out uint bytes);
            if (status == 0) return HasCodexCommandLine((UnicodeString*)data);
            if (bytes <= stackBytes || bytes > 65536 + sizeof(UnicodeString)) return true;

            // Large command lines are read only when necessary and are never logged or retained.
            byte* large = (byte*)NativeMemory.Alloc(bytes);
            try
            {
                status = NtQueryInformationProcess(process, commandLineInformation, large, bytes, out _);
                return status != 0 || HasCodexCommandLine((UnicodeString*)large);
            }
            finally { NativeMemory.Free(large); }
        }
        finally { CloseHandle(process); }
    }

    // An elevated node.exe can deny access, and an exited process can return invalid parameter.
    internal static bool NodeMayBeCodexAfterOpenFailure(int error) => error is not (5 or 87);

    private static unsafe bool HasCodexCommandLine(UnicodeString* line)
    {
        ReadOnlySpan<char> command = new(line->Text, line->Length / sizeof(char));
        return command.Contains("\\codex.js", StringComparison.OrdinalIgnoreCase) ||
            command.Contains("/codex.js", StringComparison.OrdinalIgnoreCase) ||
            command.Contains("@openai\\codex", StringComparison.OrdinalIgnoreCase) ||
            command.Contains("@openai/codex", StringComparison.OrdinalIgnoreCase) ||
            command.Contains(" codex.js", StringComparison.OrdinalIgnoreCase) ||
            command.Contains("\"codex.js\"", StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task<(int ExitCode, string? Output)> RunAsync(ProcessStartInfo start, bool captureVersion, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var job = CreateJobObjectW(0, 0);
        if (job.IsInvalid || !KillOnClose(job)) throw new Win32Exception();
        using var process = Process.Start(start)!;

        try
        {
            if (AssignProcessToJobObject(job, process.SafeHandle) == 0) throw new Win32Exception();
            ct.ThrowIfCancellationRequested();
            process.StandardInput.WriteLine("start");
            process.StandardInput.Close();

            var output = captureVersion ? ReadVersionAsync(process.StandardOutput, ct) : null;
            Task stdout = output is not null ? output : process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, 4096, ct);
            Task stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, 4096, ct);
            await Task.WhenAll(process.WaitForExitAsync(ct), stdout, stderr).WaitAsync(ct).ConfigureAwait(false);
            while (HasActiveProcesses(job)) await Task.Delay(25, ct).ConfigureAwait(false);
            return (process.ExitCode, output?.Result);
        }
        finally
        {
            job.Dispose();
            // On assignment failure the still-gated shell is outside the job.
            await Login.StopCli(process).ConfigureAwait(false);
        }
    }

    private static async Task<string> ReadVersionAsync(StreamReader reader, CancellationToken ct)
    {
        var buffer = new char[1024];
        int length = await reader.ReadBlockAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
        if (length == buffer.Length)
        {
            await reader.BaseStream.CopyToAsync(Stream.Null, 4096, ct).ConfigureAwait(false);
            return "";
        }
        return new string(buffer, 0, length);
    }

    private static unsafe bool KillOnClose(SafeFileHandle job)
    {
        var limits = new JobLimits { Basic = new() { Flags = 0x2000 } }; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        return SetInformationJobObject(job, 9, &limits, (uint)sizeof(JobLimits)) != 0;
    }

    private static unsafe bool HasActiveProcesses(SafeFileHandle job)
    {
        JobAccounting accounting;
        if (QueryInformationJobObject(job, 1, &accounting, (uint)sizeof(JobAccounting), 0) == 0) throw new Win32Exception();
        return accounting.ActiveProcesses != 0;
    }

    private unsafe struct ProcessEntry
    {
        internal uint Size, Usage, Id;
        internal nuint DefaultHeap;
        internal uint ModuleId, Threads, ParentId;
        internal int BasePriority;
        internal uint Flags;
        internal fixed char Name[260];
    }

    // The native query populates these fields directly.
#pragma warning disable CS0649
    private unsafe struct UnicodeString { internal ushort Length, Capacity; internal char* Text; }
#pragma warning restore CS0649

    private struct BasicJobLimits
    {
        internal long ProcessTime, JobTime;
        internal uint Flags;
        internal nuint MinimumWorkingSet, MaximumWorkingSet;
        internal uint ActiveProcesses;
        internal nuint Affinity;
        internal uint PriorityClass, SchedulingClass;
    }

    private unsafe struct JobLimits
    {
        internal BasicJobLimits Basic;
        internal fixed ulong IoCounters[6];
        internal nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    private struct JobAccounting
    {
        internal long UserTime, KernelTime, ThisPeriodUserTime, ThisPeriodKernelTime;
        internal uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }

    [LibraryImport("kernel32", SetLastError = true)]
    private static partial nint CreateToolhelp32Snapshot(uint flags, uint processId);
    [LibraryImport("kernel32", SetLastError = true)]
    private static partial int Process32FirstW(SafeFileHandle snapshot, ref ProcessEntry entry);
    [LibraryImport("kernel32", SetLastError = true)]
    private static partial int Process32NextW(SafeFileHandle snapshot, ref ProcessEntry entry);
    [LibraryImport("kernel32", SetLastError = true)]
    private static partial nint OpenProcess(uint access, int inherit, uint processId);
    [LibraryImport("kernel32")]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);
    [LibraryImport("kernel32")]
    private static partial int CloseHandle(nint handle);
    [LibraryImport("ntdll")]
    private static unsafe partial int NtQueryInformationProcess(nint process, int kind, void* buffer, uint size, out uint bytes);
    [LibraryImport("kernel32", SetLastError = true)]
    private static partial SafeFileHandle CreateJobObjectW(nint attributes, nint name);
    [LibraryImport("kernel32", SetLastError = true)]
    private static unsafe partial int SetInformationJobObject(SafeFileHandle job, int kind, void* buffer, uint size);
    [LibraryImport("kernel32", SetLastError = true)]
    private static unsafe partial int QueryInformationJobObject(SafeFileHandle job, int kind, void* buffer, uint size, nint bytes);
    [LibraryImport("kernel32", SetLastError = true)]
    private static partial int AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
}
