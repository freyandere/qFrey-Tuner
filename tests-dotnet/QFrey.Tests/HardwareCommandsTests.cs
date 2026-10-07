using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using QFrey.Core.Contracts;
using QFrey.Core.Qbittorrent;
using QFrey.Desktop.Bridge;
using QFrey.Desktop.Platform;
using Xunit;

namespace QFrey.Tests;

public sealed class HardwareCommandsTests
{
    private static readonly DateTimeOffset SampledAt = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MapperPreservesObservedUnitsAndCreatesInputsOnlyFromCompleteFacts()
    {
        var observation = new HardwareObservation(
            new("fresh", 16UL * 1024 * 1024 * 1024, "memory-api", null),
            new("fresh", 16, "logical-processors", null),
            new("fresh", 8, "efficiency-classes", null),
            "Nvme", "fresh", "storage-ioctl", null);

        var mapped = BridgeDispatcher.MapHardwareObservation(observation, "AB12", SampledAt);

        Assert.NotNull(mapped.Inputs);
        Assert.Equal(StorageType.Nvme, mapped.Inputs!.StorageType);
        Assert.Equal(16, mapped.Inputs.RamGiB);
        Assert.Equal(16, mapped.Inputs.CpuCores);
        Assert.True(mapped.Inputs.IsHybridCpu);
        Assert.Equal(8, mapped.Inputs.PerformanceCores);
        Assert.Equal(InputSource.LocalDetection, mapped.Inputs.Source);
        Assert.Equal(new FreshReading(16UL * 1024 * 1024 * 1024, SampledAt), mapped.Facts.RamBytes);
        Assert.Equal(new FreshReading(16, SampledAt), mapped.Facts.LogicalCpuCount);
        Assert.Equal(StorageType.Nvme, mapped.Facts.StorageType);
        Assert.Equal("AB12", mapped.Facts.VolumeToken);
        Assert.Empty(mapped.ReasonCodes);
    }

    [Fact]
    public void MapperKeepsUnavailableAndInvalidReadingsTypedAndDoesNotInventZeroInputs()
    {
        var observation = new HardwareObservation(
            new("unavailable", null, "memory-api", "ramUnavailable"),
            new("fresh", 0, "logical-processors", null),
            new("unavailable", null, "efficiency-classes", "cpuTopologyUnavailable"),
            "unknown", "fresh", "storage-ioctl", null);

        var mapped = BridgeDispatcher.MapHardwareObservation(observation, null, SampledAt);

        Assert.Null(mapped.Inputs);
        Assert.Equal(new UnavailableReading("ramUnavailable"), mapped.Facts.RamBytes);
        Assert.Equal(new ErrorReading("hardwareReadingInvalid"), mapped.Facts.LogicalCpuCount);
        Assert.Equal(new UnavailableReading("cpuTopologyUnavailable"), mapped.Facts.PerformanceCpuCount);
        Assert.Null(mapped.Facts.StorageType);
        Assert.Equal("storageTypeInvalid", mapped.Facts.StorageReasonCode);
        Assert.Contains("hardwareReadingInvalid", mapped.ReasonCodes);
        Assert.Contains("storageTypeInvalid", mapped.ReasonCodes);
    }

    [Fact]
    public async Task RemoteDetectionConsumesNativeVolumeTokenWithoutProbingOrExposingPath()
    {
        var root = NewRoot();
        var volumePath = Path.Combine(root, "selected-volume");
        var api = new HardwareApi();
        var probeCalls = 0;
        var events = Channel.CreateUnbounded<SnapshotEvent>();
        var bridge = new BridgeDispatcher(root,
            (payload, token) => QbittorrentSession.ConnectAsync(payload, token, api),
            selectFile: (_, _, _) => Task.FromResult<string?>(volumePath));
        bridge.HardwareProbe = _ => { Interlocked.Increment(ref probeCalls); throw new InvalidOperationException(); };
        bridge.SnapshotPublished += json =>
        {
            var snapshotEvent = JsonSerializer.Deserialize<SnapshotEvent>(json, Protocol.Json);
            if (snapshotEvent is not null) events.Writer.TryWrite(snapshotEvent);
            return Task.CompletedTask;
        };

        try
        {
            var connected = Read(await bridge.DispatchAsync(Request("Connect",
                new ConnectPayload("http://192.0.2.1:8080", new BypassAuthentication()), revision: 0), default));
            Assert.True(connected.Ok, connected.Error?.Code);
            var target = connected.Data!.Target!;
            Assert.False(target.IsLocal);

            var selectionJson = await bridge.DispatchAsync(Request("SelectNativeFile", new SelectFilePayload("volume")), default);
            var selection = JsonSerializer.Deserialize<CommandReply<NativeSelection?>>(selectionJson, Protocol.Json)!;
            Assert.True(selection.Ok);
            var volumeToken = selection.Data!.Token;

            var response = await bridge.DispatchAsync(Request("DetectLocalHardware", new DetectHardwarePayload(volumeToken),
                target.SessionId, connected.Revision), default);
            var accepted = JsonSerializer.Deserialize<CommandReply<AcceptedOperation>>(response, Protocol.Json)!;
            Assert.True(accepted.Ok, accepted.Error?.Code);
            Assert.DoesNotContain(volumePath, response, StringComparison.Ordinal);
            var completed = await ReadCompletion(events.Reader, accepted.Data!.OperationId);

            Assert.Equal(0, Volatile.Read(ref probeCalls));
            Assert.Equal(new UnavailableReading("remoteTarget"), completed.Snapshot.Hardware!.Facts.RamBytes);
            Assert.Equal("remoteTarget", Assert.Single(completed.Snapshot.Hardware.ReasonCodes));
            Assert.Equal(volumeToken, completed.Snapshot.Hardware.Facts.VolumeToken);
            var wire = JsonSerializer.Serialize(completed, Protocol.Json);
            Assert.DoesNotContain(volumePath, wire, StringComparison.Ordinal);
            Assert.Equal(0, api.WriteCalls);
        }
        finally
        {
            await bridge.ShutdownAsync();
            bridge.Dispose();
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public async Task CancelKeepsProbeOwnedUntilItReturnsAndDiscardsItsResult()
    {
        var root = NewRoot();
        var api = new HardwareApi();
        var selectedPath = Path.Combine(root, "volume-under-test");
        var probeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = Channel.CreateUnbounded<SnapshotEvent>();
        var probeCalls = 0;
        string? observedPath = null;
        var bridge = new BridgeDispatcher(root,
            (payload, token) => QbittorrentSession.ConnectAsync(payload, token, api),
            selectFile: (_, _, _) => Task.FromResult<string?>(selectedPath));
        bridge.HardwareProbe = path =>
        {
            Interlocked.Increment(ref probeCalls);
            observedPath = path;
            probeStarted.TrySetResult();
            releaseProbe.Task.GetAwaiter().GetResult();
            return FreshObservation();
        };
        bridge.SnapshotPublished += json =>
        {
            var snapshotEvent = JsonSerializer.Deserialize<SnapshotEvent>(json, Protocol.Json);
            if (snapshotEvent is not null) events.Writer.TryWrite(snapshotEvent);
            return Task.CompletedTask;
        };

        try
        {
            var connected = Read(await bridge.DispatchAsync(Request("Connect",
                new ConnectPayload("http://127.0.0.1:8080", new BypassAuthentication()), revision: 0), default));
            Assert.True(connected.Ok, connected.Error?.Code);
            var target = connected.Data!.Target!;
            var selection = JsonSerializer.Deserialize<CommandReply<NativeSelection?>>(
                await bridge.DispatchAsync(Request("SelectNativeFile", new SelectFilePayload("volume")), default), Protocol.Json)!;
            Assert.True(selection.Ok);

            var accepted = JsonSerializer.Deserialize<CommandReply<AcceptedOperation>>(
                await bridge.DispatchAsync(Request("DetectLocalHardware", new DetectHardwarePayload(selection.Data!.Token),
                    target.SessionId, connected.Revision), default), Protocol.Json)!;
            Assert.True(accepted.Ok, accepted.Error?.Code);
            await probeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(Path.GetFullPath(selectedPath), observedPath);

            var cancelled = Read(await bridge.DispatchAsync(Request("CancelOperation",
                new CancelOperationPayload(accepted.Data!.OperationId), target.SessionId, accepted.Revision), default));
            Assert.True(cancelled.Ok, cancelled.Error?.Code);
            Assert.Equal("cancelling", cancelled.Data!.ActiveOperation!.Stage);

            var conflict = Read(await bridge.DispatchAsync(Request("DetectLocalHardware", new DetectHardwarePayload(null),
                target.SessionId, cancelled.Revision), default));
            Assert.False(conflict.Ok);
            Assert.Equal(ErrorCodes.OperationConflict, conflict.Error!.Code);
            Assert.Equal(1, Volatile.Read(ref probeCalls));

            releaseProbe.TrySetResult();
            var completed = await ReadCompletion(events.Reader, accepted.Data.OperationId);
            Assert.Null(completed.Snapshot.ActiveOperation);
            Assert.Null(completed.Snapshot.Hardware!.Inputs);
            Assert.Equal(new UnavailableReading("hardwareDetectionCancelled"), completed.Snapshot.Hardware.Facts.RamBytes);
            Assert.Equal(0, api.WriteCalls);
        }
        finally
        {
            releaseProbe.TrySetResult();
            await bridge.ShutdownAsync();
            bridge.Dispose();
            DeleteTestRoot(root);
        }
    }

    private static HardwareObservation FreshObservation() => new(
        new("fresh", 16UL * 1024 * 1024 * 1024, "memory-api", null),
        new("fresh", 16, "logical-processors", null),
        new("fresh", 8, "efficiency-classes", null),
        "Nvme", "fresh", "storage-ioctl", null);

    private static string Request(string command, object payload, Guid? sessionId = null, long? revision = null) =>
        JsonSerializer.Serialize(new
        {
            protocolVersion = Protocol.Version,
            requestId = Guid.NewGuid(),
            command,
            targetSessionId = sessionId,
            expectedRevision = revision,
            payload
        }, Protocol.Json);

    private static Reply Read(string json) => JsonSerializer.Deserialize<Reply>(json, Protocol.Json)!;

    private static async Task<SnapshotEvent> ReadCompletion(ChannelReader<SnapshotEvent> events, Guid operationId)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var item = await events.ReadAsync(deadline.Token);
            if (item.OperationId == operationId && item.Snapshot.ActiveOperation is null) return item;
        }
    }

    private static string NewRoot()
    {
        var root = Path.Combine(ProjectRoot(), ".cache", "tests", "hardware-commands", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTestRoot(string root)
    {
        var fullPath = Path.GetFullPath(root);
        var parent = Path.GetFullPath(Path.Combine(ProjectRoot(), ".cache", "tests", "hardware-commands"));
        if (!string.Equals(Path.GetDirectoryName(fullPath), parent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to delete outside the test cache.");
        if (Directory.Exists(fullPath)) Directory.Delete(fullPath, recursive: true);
    }

    private static string ProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "pyproject.toml")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Project root not found.");
    }

    private sealed class HardwareApi : HttpMessageHandler
    {
        private static readonly string Preferences = ReadPreferences();
        private int writeCalls;
        public int WriteCalls => Volatile.Read(ref writeCalls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method != HttpMethod.Get) Interlocked.Increment(ref writeCalls);
            var route = string.Join('/', request.RequestUri!.AbsolutePath.Trim('/').Split('/').TakeLast(2));
            var body = route switch
            {
                "app/version" => "v5.1.0",
                "app/webapiVersion" => "2.11.0",
                "app/buildInfo" => "{\"libtorrent\":\"2.0.11\"}",
                "app/preferences" => Preferences,
                "app/networkInterfaceList" => "[]",
                "transfer/info" => "{}",
                "torrents/info" => "[]",
                _ => throw new InvalidOperationException("Unexpected mock API route: " + route)
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }

        private static string ReadPreferences()
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(ProjectRoot(),
                "tests-contract/fixtures/legacy/settings-payloads.json")));
            return document.RootElement.GetProperty("2.0.11").GetProperty("payload").GetRawText();
        }
    }
}
