using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using QFrey.Core.Contracts;
using QFrey.Core.Platform;

namespace QFrey.Desktop.Platform;

// Called by the one target collector. Local readings have a separate two-second cadence.
public sealed class WindowsProcessMetrics(string endpoint, TimeProvider? clock = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private ProcessIdentity? identity;
    private long timestamp;
    private TimeSpan cpu;
    private IoCounters? io;
    private LiveMetric[]? latest;

    public LiveMetric[] Sample()
    {
        var now = time.GetTimestamp();
        if (latest is not null && time.GetElapsedTime(timestamp, now) < TimeSpan.FromSeconds(2)) return latest.ToArray();
        var owner = WindowsPlatformProbe.FindOwner(endpoint);
        if (owner.Status != OwnerLookupStatus.Available || owner.Owner is null)
        {
            identity = null; io = null; timestamp = now;
            return latest = Missing(owner.Status == OwnerLookupStatus.RemoteTarget ? "REMOTE_TARGET" : "PROCESS_OWNER_UNAVAILABLE");
        }
        try
        {
            using var process = Process.GetProcessById(owner.Owner.ProcessId);
            if (process.StartTime.ToUniversalTime().Ticks != owner.Owner.StartTimeUtcTicks) throw new InvalidOperationException();
            var totalCpu = process.TotalProcessorTime;
            var privateBytes = process.PrivateMemorySize64;
            using var query = OpenProcess(0x1000, false, owner.Owner.ProcessId); // PROCESS_QUERY_LIMITED_INFORMATION
            IoCounters? currentIo = !query.IsInvalid && GetProcessIoCounters(query, out var counters) ? counters : null;
            // Reprove port ownership after reading; a PID or endpoint switch invalidates the whole sample.
            if (WindowsPlatformProbe.FindOwner(endpoint).Owner != owner.Owner) throw new InvalidOperationException();
            var sampled = time.GetUtcNow();
            var elapsed = time.GetElapsedTime(timestamp, now).TotalSeconds;
            var same = identity == owner.Owner && elapsed > 0;
            var capacity = GetActiveProcessorCount(0xffff);
            MetricReading cpuReading = same && capacity > 0 && totalCpu >= cpu
                ? new FreshReading(Math.Clamp((totalCpu - cpu).TotalSeconds / elapsed / capacity * 100, 0, 100), sampled)
                : new UnavailableReading("FIRST_DELTA");
            MetricReading IoReading(bool read)
            {
                if (currentIo is null) return new UnavailableReading("PROCESS_IO_UNAVAILABLE");
                if (!same || io is null) return new UnavailableReading("FIRST_DELTA");
                var before = read ? io.Value.ReadTransferCount : io.Value.WriteTransferCount;
                var after = read ? currentIo.Value.ReadTransferCount : currentIo.Value.WriteTransferCount;
                return after >= before ? new FreshReading((after - before) / elapsed, sampled) : new UnavailableReading("COUNTER_RESET");
            }
            latest = [Metric("local.cpu", MetricUnit.Percent, "Process.TotalProcessorTime / elapsed / all logical CPUs", cpuReading),
                Metric("local.privateMemory", MetricUnit.Bytes, "Process.PrivateMemorySize64", new FreshReading(privateBytes, sampled)),
                Metric("local.ioRead", MetricUnit.BytesPerSecond, "GetProcessIoCounters ReadTransferCount", IoReading(true)),
                Metric("local.ioWrite", MetricUnit.BytesPerSecond, "GetProcessIoCounters WriteTransferCount", IoReading(false))];
            identity = owner.Owner; cpu = totalCpu; io = currentIo; timestamp = now;
            return latest.ToArray();
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            identity = null; io = null; timestamp = now;
            return latest = Missing("PROCESS_READING_UNAVAILABLE");
        }
    }

    private static LiveMetric Metric(string id, MetricUnit unit, string source, MetricReading reading) => new(id, unit, MetricScope.LocalProcess, source, reading);
    private static LiveMetric[] Missing(string reason) => [
        Metric("local.cpu", MetricUnit.Percent, "Process.TotalProcessorTime", new UnavailableReading(reason)),
        Metric("local.privateMemory", MetricUnit.Bytes, "Process.PrivateMemorySize64", new UnavailableReading(reason)),
        Metric("local.ioRead", MetricUnit.BytesPerSecond, "GetProcessIoCounters", new UnavailableReading(reason)),
        Metric("local.ioWrite", MetricUnit.BytesPerSecond, "GetProcessIoCounters", new UnavailableReading(reason))];
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }
    [DllImport("kernel32.dll")] private static extern uint GetActiveProcessorCount(ushort groupNumber);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(SafeProcessHandle process, out IoCounters counters);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
}
