using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using QFrey.Core.Platform;

namespace QFrey.Desktop.Platform;

// Process arguments may contain credentials. This capability is intentionally memory-only and redacted.
public sealed class ProcessLaunchContext
{
    private readonly string[] arguments;
    private ProcessLaunchContext(ProcessIdentity identity, string executablePath, string workingDirectory, string[] arguments)
    {
        Identity = identity;
        ExecutablePath = executablePath;
        WorkingDirectory = workingDirectory;
        this.arguments = arguments;
    }

    internal ProcessIdentity Identity { get; }
    internal string ExecutablePath { get; }
    internal string WorkingDirectory { get; }
    internal IReadOnlyList<string> Arguments => Array.AsReadOnly(arguments);
    public override string ToString() => "ProcessLaunchContext [redacted]";

    internal static bool TryCapture(ProcessIdentity expected, out ProcessLaunchContext? context, out string reasonCode)
    {
        context = null;
        reasonCode = "launchContextUnavailable";
        var os = Environment.OSVersion.Version;
        if (IntPtr.Size != 8 || Environment.OSVersion.Platform != PlatformID.Win32NT || os.Major != 10
            || os.Build is < 19041 or > 26100)
        { reasonCode = "launchContextPlatformUnsupported"; return false; }
        var handle = OpenProcess(ProcessQueryLimitedInformation | ProcessVmRead, false, expected.ProcessId);
        if (handle == IntPtr.Zero)
        { reasonCode = Marshal.GetLastWin32Error() == ErrorAccessDenied ? "launchContextAccessDenied" : "launchContextUnavailable"; return false; }
        try
        {
            if (!IsNativeX64(handle)) { reasonCode = "launchContextBitnessUnsupported"; return false; }
            if (!HasIdentity(expected)) { reasonCode = "processIdentityChanged"; return false; }
            var image = ReadImagePath(handle);
            var localImage = image is null ? null : NormalizeLocalPath(image);
            if (localImage is null || !Path.GetFileName(localImage).Equals("qbittorrent.exe", StringComparison.OrdinalIgnoreCase)
                || !File.Exists(localImage))
            { reasonCode = "launchExecutableUnavailable"; return false; }

            if (!TryReadProcessParameters(handle, localImage, out var commandLine, out var workingDirectory))
            { reasonCode = "launchParametersUnavailable"; return false; }
            if (!TryParseArguments(commandLine, out var argv) || argv.Length == 0
                || !SameLocalPath(argv[0], localImage))
            { reasonCode = "launchArgumentsUnverifiable"; return false; }

            workingDirectory = NormalizeLocalPath(workingDirectory);
            if (workingDirectory is null || !Directory.Exists(workingDirectory))
            { reasonCode = "launchWorkingDirectoryUnavailable"; return false; }
            if (!TryReadProcessParameters(handle, localImage, out var commandLineAfter, out var workingDirectoryAfter)
                || !string.Equals(commandLine, commandLineAfter, StringComparison.Ordinal)
                || !string.Equals(workingDirectory, NormalizeLocalPath(workingDirectoryAfter), StringComparison.OrdinalIgnoreCase))
            { reasonCode = "launchParametersChangedDuringRead"; return false; }
            if (!HasIdentity(expected)) { reasonCode = "processIdentityChanged"; return false; }

            context = new(expected, localImage, workingDirectory, argv[1..]);
            reasonCode = "";
            return true;
        }
        finally { CloseHandle(handle); }
    }

    private static bool IsNativeX64(IntPtr targetProcess)
    {
        try
        {
            return IsWow64Process2(GetCurrentProcess(), out var callerMachine, out var hostMachine)
                && callerMachine == ImageFileMachineUnknown && hostMachine == ImageFileMachineAmd64
                && IsWow64Process2(targetProcess, out var targetMachine, out var targetHostMachine)
                && targetMachine == ImageFileMachineUnknown && targetHostMachine == ImageFileMachineAmd64;
        }
        catch (EntryPointNotFoundException) { return false; }
    }

    private static bool HasIdentity(ProcessIdentity expected)
    {
        try
        {
            using var process = Process.GetProcessById(expected.ProcessId);
            return TargetOwnership.SameProcess(expected,
                new(expected.ProcessId, process.StartTime.ToUniversalTime().Ticks));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        { return false; }
    }

    private static string? ReadImagePath(IntPtr handle)
    {
        var buffer = new StringBuilder(32768);
        var length = buffer.Capacity;
        return QueryFullProcessImageNameW(handle, 0, buffer, ref length) && length > 0
            ? buffer.ToString(0, length) : null;
    }

    private static bool TryReadProcessParameters(IntPtr process, string expectedImage, out string commandLine, out string workingDirectory)
    {
        commandLine = workingDirectory = "";
        var info = new ProcessBasicInformation();
        if (NtQueryInformationProcess is null
            || NtQueryInformationProcess(process, 0, ref info, (uint)Marshal.SizeOf<ProcessBasicInformation>(), out _) < 0
            || info.PebBaseAddress == IntPtr.Zero) return false;
        if (!ReadPointer(process, IntPtr.Add(info.PebBaseAddress, 0x20), out var parameters) || parameters == IntPtr.Zero)
            return false;

        // x64 RTL_USER_PROCESS_PARAMETERS: CurrentDirectory at 0x38; ImagePathName at 0x60; CommandLine at 0x70.
        // Verify ImagePathName before trusting these version-sensitive offsets.
        if (!ReadUnicodeString(process, IntPtr.Add(parameters, 0x60), out var parameterImage)
            || !SameLocalPath(parameterImage, expectedImage)
            || !ReadUnicodeString(process, IntPtr.Add(parameters, 0x38), out workingDirectory)
            || !ReadUnicodeString(process, IntPtr.Add(parameters, 0x70), out commandLine)) return false;
        return true;
    }

    private static bool ReadPointer(IntPtr process, IntPtr address, out IntPtr value)
    {
        value = IntPtr.Zero;
        var bytes = new byte[8];
        return ReadExact(process, address, bytes) && (value = new IntPtr(BitConverter.ToInt64(bytes))) != IntPtr.Zero;
    }

    private static bool ReadUnicodeString(IntPtr process, IntPtr address, out string value)
    {
        value = "";
        var data = new byte[16];
        if (!ReadExact(process, address, data)) return false;
        var length = BitConverter.ToUInt16(data, 0);
        var maximumLength = BitConverter.ToUInt16(data, 2);
        var buffer = BitConverter.ToInt64(data, 8);
        if (length == 0 || length > maximumLength || (length & 1) != 0 || length > 32766 || buffer == 0) return false;
        var text = new byte[length];
        if (!ReadExact(process, new IntPtr(buffer), text)) return false;
        value = Encoding.Unicode.GetString(text);
        return !value.Contains('\0');
    }

    private static bool ReadExact(IntPtr process, IntPtr address, byte[] buffer) =>
        ReadProcessMemory(process, address, buffer, (nuint)buffer.Length, out var read) && read == (nuint)buffer.Length;

    private static bool TryParseArguments(string commandLine, out string[] arguments)
    {
        arguments = [];
        var pointer = CommandLineToArgvW(commandLine, out var count);
        if (pointer == IntPtr.Zero) return false;
        try
        {
            if (count is < 1 or > 1024) return false;
            var parsed = new string[count];
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.ReadIntPtr(pointer, i * IntPtr.Size);
                parsed[i] = Marshal.PtrToStringUni(item) ?? "";
                if (parsed[i].Length > 32767 || parsed[i].Contains('\0')) return false;
            }
            arguments = parsed;
            return true;
        }
        finally { _ = LocalFree(pointer); }
    }

    private static bool SameLocalPath(string candidate, string expected)
    {
        var normalized = NormalizeLocalPath(candidate);
        return normalized is not null && string.Equals(normalized, Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeLocalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("\\Device\\", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("\\??\\UNC\\", StringComparison.OrdinalIgnoreCase)) return null;
        if (path.StartsWith("\\??\\", StringComparison.Ordinal) || path.StartsWith("\\\\?\\", StringComparison.Ordinal))
            path = path[4..];
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal)) return null;
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        { return null; }
    }

    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorAccessDenied = 5;
    private const ushort ImageFileMachineUnknown = 0x0000;
    private const ushort ImageFileMachineAmd64 = 0x8664;
    private static readonly NtQueryInformationProcessDelegate? NtQueryInformationProcess = LoadNtQueryInformationProcess();

    private static NtQueryInformationProcessDelegate? LoadNtQueryInformationProcess()
    {
        if (!NativeLibrary.TryLoad("ntdll.dll", out var library)) return null;
        if (NativeLibrary.TryGetExport(library, "NtQueryInformationProcess", out var symbol))
            return Marshal.GetDelegateForFunctionPointer<NtQueryInformationProcessDelegate>(symbol);
        NativeLibrary.Free(library);
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public int ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public int BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder imageName, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, nuint size, out nuint read);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int NtQueryInformationProcessDelegate(IntPtr process, int informationClass,
        ref ProcessBasicInformation information, uint informationLength, out uint returnedLength);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
