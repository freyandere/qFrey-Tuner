using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using QFrey.Core.Platform;

namespace QFrey.Desktop.Platform;

public sealed record ProcessOwnerObservation(OwnerLookupStatus Status, ProcessIdentity? Owner, string? ReasonCode,
    bool RestartBlocked, string? RestartReasonCode)
{
    internal ProcessLaunchContext? LaunchContext { get; init; }
}
public sealed record HardwareReading<T>(string Status, T? Value, string Source, string? ReasonCode) where T : struct;
public sealed record HardwareObservation(HardwareReading<ulong> RamBytes, HardwareReading<int> LogicalCpuCount,
    HardwareReading<int> PerformanceCpuCount, string? StorageType, string StorageStatus, string StorageSource,
    string? StorageReasonCode);

public static class WindowsPlatformProbe
{
    private const int ErrorAccessDenied = 5;
    [DllImport("kernel32.dll")] private static extern uint GetActiveProcessorCount(ushort groupNumber);
    private const int ErrorInsufficientBuffer = 122;
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidListener = 3;

    public static ProcessOwnerObservation FindOwner(string endpoint, bool captureLaunchContext = false)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return Unavailable(OwnerLookupStatus.Unknown, "invalidEndpoint");
        if (!uri.IsLoopback) return Unavailable(OwnerLookupStatus.RemoteTarget, "remoteTarget");

        var pids = new HashSet<int>();
        var denied = false;
        var invalidOwner = false;
        foreach (var family in new[] { AfInet, AfInet6 })
        {
            var result = ReadListeners(family, uri, pids, ref invalidOwner);
            denied |= result == ErrorAccessDenied;
            if (result != 0 && result != ErrorAccessDenied)
                return Unavailable(OwnerLookupStatus.Unknown, "listenerTableUnavailable");
        }
        if (invalidOwner) return Unavailable(OwnerLookupStatus.Unknown, "listenerOwnerUnavailable");

        var resolved = TargetOwnership.Resolve(true, pids, denied);
        if (resolved.Status != OwnerLookupStatus.Available || resolved.ProcessId is not int pid)
            return Unavailable(resolved.Status, resolved.ReasonCode);

        try
        {
            using var process = Process.GetProcessById(pid);
            var identity = new ProcessIdentity(pid, process.StartTime.ToUniversalTime().Ticks);
            if (!string.Equals(process.ProcessName, "qbittorrent", StringComparison.OrdinalIgnoreCase))
                return Unavailable(OwnerLookupStatus.Unknown, "listenerIsNotQbittorrent");
            if (!IdentityStillOwnsPort(endpoint, identity))
                return Unavailable(OwnerLookupStatus.Unknown, "ownerChangedDuringLookup");
            if (!captureLaunchContext)
                return new(OwnerLookupStatus.Available, identity, null, true, "launchContextNotRequested");
            if (!ProcessLaunchContext.TryCapture(identity, out var context, out var restartReason) || context is null)
                return new(OwnerLookupStatus.Available, identity, null, true, restartReason);
            if (!IdentityStillOwnsPort(endpoint, identity))
                return Unavailable(OwnerLookupStatus.Unknown, "ownerChangedDuringLookup");
            return new(OwnerLookupStatus.Available, identity, null, false, null) { LaunchContext = context };
        }
        catch (ArgumentException) { return Unavailable(OwnerLookupStatus.Unknown, "ownerExitedDuringLookup"); }
        catch (InvalidOperationException) { return Unavailable(OwnerLookupStatus.Unknown, "ownerIdentityUnavailable"); }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == ErrorAccessDenied)
        { return Unavailable(OwnerLookupStatus.AccessDenied, "ownerAccessDenied"); }
        catch (System.ComponentModel.Win32Exception)
        { return Unavailable(OwnerLookupStatus.Unknown, "ownerIdentityUnavailable"); }
        catch (UnauthorizedAccessException)
        { return Unavailable(OwnerLookupStatus.AccessDenied, "ownerAccessDenied"); }
    }

    public static bool IsSameOwner(ProcessIdentity expected, ProcessIdentity observed) =>
        TargetOwnership.SameProcess(expected, observed);

    public static HardwareObservation ReadHardware(string? selectedVolumePath)
    {
        var ram = ReadRam();
        var count = GetActiveProcessorCount(0xffff);
        var logical = count is > 0 and <= 4096
            ? new HardwareReading<int>("fresh", (int)count, "GetActiveProcessorCount(ALL_PROCESSOR_GROUPS)", null)
            : new HardwareReading<int>("unavailable", null, "GetActiveProcessorCount", "cpuCountUnavailable");
        var performance = ReadPerformanceCores();
        var (storage, storageStatus, storageSource, storageReason) = ReadSelectedStorage(selectedVolumePath);
        return new(ram, logical, performance, storage, storageStatus, storageSource, storageReason);
    }

    private static ProcessOwnerObservation Unavailable(OwnerLookupStatus status, string? reason) =>
        new(status, null, reason, true, "verifiedOwnerAndLaunchContextRequired");

    private static bool IdentityStillOwnsPort(string endpoint, ProcessIdentity identity)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) return false;
        var pids = new HashSet<int>();
        var invalidOwner = false;
        foreach (var family in new[] { AfInet, AfInet6 })
            if (ReadListeners(family, uri, pids, ref invalidOwner) != 0) return false;
        if (invalidOwner) return false;
        if (!pids.SetEquals([identity.ProcessId])) return false;
        try
        {
            using var current = Process.GetProcessById(identity.ProcessId);
            return TargetOwnership.SameProcess(identity, new(identity.ProcessId, current.StartTime.ToUniversalTime().Ticks));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        { return false; }
    }

    private static int ReadListeners(int family, Uri uri, HashSet<int> pids, ref bool invalidOwner)
    {
        uint size = 0;
        var result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TcpTableOwnerPidListener, 0);
        if (result == 0) return 0;
        if (result != ErrorInsufficientBuffer) return result;
        if (size < sizeof(uint) || size > 16 * 1024 * 1024) return 13;
        var allocatedSize = size;
        var buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            result = GetExtendedTcpTable(buffer, ref size, false, family, TcpTableOwnerPidListener, 0);
            if (result != 0) return result;
            var count = Marshal.ReadInt32(buffer);
            var rowSize = family == AfInet ? Marshal.SizeOf<TcpRowOwnerPid>() : Marshal.SizeOf<Tcp6RowOwnerPid>();
            if (count < 0 || sizeof(uint) + (long)count * rowSize > allocatedSize) return 13;
            var cursor = IntPtr.Add(buffer, sizeof(uint));
            for (var i = 0; i < count; i++, cursor = IntPtr.Add(cursor, rowSize))
            {
                if (family == AfInet)
                {
                    var row = Marshal.PtrToStructure<TcpRowOwnerPid>(cursor);
                    if (HostPort(row.LocalPort) == uri.Port && IsRequestedV4(row.LocalAddress, uri))
                        AddOwner(row.ProcessId, pids, ref invalidOwner);
                }
                else
                {
                    var row = Marshal.PtrToStructure<Tcp6RowOwnerPid>(cursor);
                    if (HostPort(row.LocalPort) == uri.Port && IsRequestedV6(row.LocalAddress, row.LocalScopeId, uri))
                        AddOwner(row.ProcessId, pids, ref invalidOwner);
                }
            }
            return 0;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static ushort HostPort(uint networkPort)
    {
        var value = (ushort)networkPort;
        return BitConverter.IsLittleEndian ? (ushort)((value >> 8) | (value << 8)) : value;
    }

    private static void AddOwner(uint processId, HashSet<int> pids, ref bool invalidOwner)
    {
        if (processId is 0 or > int.MaxValue) invalidOwner = true;
        else pids.Add((int)processId);
    }

    private static bool IsRequestedV4(uint address, Uri uri)
    {
        if (address == 0) return true;
        var actual = BitConverter.GetBytes(address);
        if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return actual.SequenceEqual(new byte[] { 127, 0, 0, 1 });
        return uri.HostNameType == UriHostNameType.IPv4 && IPAddress.TryParse(uri.Host, out var requested)
            && actual.SequenceEqual(requested.GetAddressBytes());
    }

    private static bool IsRequestedV6(byte[] address, uint scopeId, Uri uri)
    {
        if (address.All(b => b == 0)) return true;
        if (scopeId != 0) return false;
        if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return IPAddress.IPv6Loopback.GetAddressBytes().SequenceEqual(address);
        return uri.HostNameType == UriHostNameType.IPv6 && IPAddress.TryParse(uri.Host.Trim('[', ']'), out var requested)
            && requested.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            && requested.GetAddressBytes().SequenceEqual(address);
    }

    private static HardwareReading<ulong> ReadRam()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (GlobalMemoryStatusEx(ref status))
            return new("fresh", status.TotalPhysical, "GlobalMemoryStatusEx", null);
        return new("unavailable", null, "GlobalMemoryStatusEx", "ramUnavailable");
    }

    private static HardwareReading<int> ReadPerformanceCores()
    {
        uint size = 0;
        _ = GetLogicalProcessorInformationEx(0, IntPtr.Zero, ref size);
        if (size == 0 || size > 1024 * 1024)
            return new("unavailable", null, "GetLogicalProcessorInformationEx", "cpuTopologyUnavailable");
        var allocatedSize = size;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (!GetLogicalProcessorInformationEx(0, buffer, ref size))
                return new("unavailable", null, "GetLogicalProcessorInformationEx", "cpuTopologyUnavailable");
            if (size == 0 || size > allocatedSize || size > 1024 * 1024 || size > int.MaxValue)
                return new("unavailable", null, "GetLogicalProcessorInformationEx", "cpuTopologyInvalid");
            var data = new byte[(int)size];
            Marshal.Copy(buffer, data, 0, data.Length);
            if (!TryParseProcessorCoreEfficiencyClasses(data, out var classes))
                return new("unavailable", null, "GetLogicalProcessorInformationEx", "cpuTopologyInvalid");
            if (classes.Count < 2)
                return new("unavailable", null, "GetLogicalProcessorInformationEx", "performanceCoresNotDistinguished");
            return new("fresh", classes.MaxBy(pair => pair.Key).Value,
                "GetLogicalProcessorInformationEx", null);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static bool TryParseProcessorCoreEfficiencyClasses(ReadOnlySpan<byte> data, out Dictionary<byte, int> classes)
    {
        classes = [];
        if (data.IsEmpty) return false;
        var offset = 0;
        while (offset < data.Length)
        {
            if (data.Length - offset < 8) return false;
            var relationship = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
            var recordSize = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset + 4, 4));
            if (recordSize < 8 || recordSize > data.Length - offset) return false;
            if (relationship == 0)
            {
                if (recordSize < 48) return false;
                var groupCount = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset + 30, 2));
                if (groupCount == 0 || groupCount > (recordSize - 32) / 16) return false;
                var efficiencyClass = data[offset + 9];
                classes[efficiencyClass] = classes.GetValueOrDefault(efficiencyClass) + 1;
            }
            offset += recordSize;
        }
        return offset == data.Length;
    }

    internal static bool TryReadSeekPenalty(ReadOnlySpan<byte> descriptor, out bool incursSeekPenalty)
    {
        incursSeekPenalty = false;
        if (descriptor.Length < 9) return false;
        var version = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[..4]);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(descriptor.Slice(4, 4));
        if (version < 9 || version > descriptor.Length || size < 9 || size > descriptor.Length || version > size)
            return false;
        incursSeekPenalty = descriptor[8] != 0;
        return true;
    }

    private static (string? Type, string Status, string Source, string? Reason) ReadSelectedStorage(string? path)
    {
        const string source = "IOCTL_STORAGE_QUERY_PROPERTY";
        if (string.IsNullOrWhiteSpace(path)) return (null, "unavailable", source, "volumeNotSelected");
        if (path.StartsWith("\\\\", StringComparison.Ordinal) || !Path.IsPathFullyQualified(path))
            return (null, "unavailable", source, "volumeNotLocalOrInvalid");
        var root = Path.GetPathRoot(path);
        if (root is null || root.Length < 2 || root[1] != ':')
            return (null, "unavailable", source, "volumeNotLocalOrInvalid");
        var volumePath = new StringBuilder(1024);
        var volumeName = new StringBuilder(1024);
        if (!GetVolumePathNameW(path, volumePath, volumePath.Capacity)
            || !GetVolumeNameForVolumeMountPointW(volumePath.ToString(), volumeName, volumeName.Capacity))
            return (null, "unavailable", source, "volumeQueryDeniedOrUnavailable");
        var handle = CreateFileW(volumeName.ToString().TrimEnd('\\'), 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle == new IntPtr(-1)) return (null, "unavailable", source, "volumeQueryDeniedOrUnavailable");
        try
        {
            var bus = QueryStorageProperty(handle, 0);
            var busValue = bus.Length >= 36 ? BitConverter.ToUInt32(bus, 28) : uint.MaxValue;
            int? busType = busValue <= int.MaxValue && BitConverter.ToUInt32(bus, 4) >= 36
                && BitConverter.ToUInt32(bus, 4) <= bus.Length ? (int)busValue : null;
            var penalty = QueryStorageProperty(handle, 7);
            bool? hasPenalty = TryReadSeekPenalty(penalty, out var incursSeekPenalty) ? incursSeekPenalty : null;
            var type = StorageDetectionPolicy.Classify(busType, hasPenalty);
            return type is null
                ? (null, "unavailable", source, "storageTypeUnavailable")
                : (type, "fresh", source, null);
        }
        finally { CloseHandle(handle); }
    }

    private static byte[] QueryStorageProperty(IntPtr handle, int propertyId)
    {
        var query = new byte[12];
        BitConverter.GetBytes(propertyId).CopyTo(query, 0);
        var output = new byte[4096];
        if (!DeviceIoControl(handle, 0x2D1400, query, query.Length, output, output.Length, out var returned, IntPtr.Zero)
            || returned > output.Length) return [];
        return output[..(int)returned];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRowOwnerPid
    { public uint State, LocalAddress, LocalPort, RemoteAddress, RemotePort, ProcessId; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Tcp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] LocalAddress;
        public uint LocalScopeId, LocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] RemoteAddress;
        public uint RemoteScopeId, RemotePort, State, ProcessId;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetExtendedTcpTable(IntPtr table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily, int tableClass, uint reserved);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint returnedLength);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetVolumePathNameW(string path, StringBuilder volumePath, int length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetVolumeNameForVolumeMountPointW(string volumePath, StringBuilder volumeName, int length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeviceIoControl(IntPtr device, uint control, byte[] input,
        int inputLength, byte[] output, int outputLength, out uint returned, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
