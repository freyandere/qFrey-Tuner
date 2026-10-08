using System.Text.Json;
using QFrey.Core.Contracts;
using QFrey.Desktop.Bridge;
using Xunit;

namespace QFrey.Tests;
public class BridgeTests
{
    [Theory]
    [InlineData("https://qfrey.local/index.html", true)]
    [InlineData("http://qfrey.local/index.html", false)]
    [InlineData("https://qfrey.local.evil.test/index.html", false)]
    [InlineData("https://qfrey.local:9999/index.html", false)]
    [InlineData("https://evil.test@qfrey.local/index.html", false)]
    [InlineData("file:///index.html", false)]
    [InlineData("https://qfrey.local/frame.html", false)]
    public void HostBoundary(string source, bool allowed) => Assert.Equal(allowed, BridgeDispatcher.IsAllowedSource(source));

    [Fact]
    public async Task ClosingLifetimeCancelsDispatchWithoutWritingPreferences()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../tests-dotnet", Guid.NewGuid().ToString("N")));
        var dispatcher = new BridgeDispatcher(root);
        using var lifetime = new CancellationTokenSource(); lifetime.Cancel();
        var request = JsonSerializer.Serialize(new
        {
            protocolVersion = 1, requestId = Guid.NewGuid(), command = "Initialize",
            targetSessionId = (Guid?)null, expectedRevision = (long?)null, payload = new { protocolVersion = 1 }
        }, Protocol.Json);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatcher.DispatchAsync(request, lifetime.Token));
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CachedReplyRemainsReplayableWhileAnotherCommandHoldsAdmission(bool holdStateGate)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../tests-dotnet", Guid.NewGuid().ToString("N")));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var dispatcher = new BridgeDispatcher(root, selectFile: async (_, _, _) =>
        {
            entered.SetResult();
            await release.Task;
            return null;
        });
        string Request(Guid id, string name, object payload) => JsonSerializer.Serialize(new
        {
            protocolVersion = 1, requestId = id, command = name,
            targetSessionId = (Guid?)null, expectedRevision = (long?)0, payload
        }, Protocol.Json);
        Task<string>? selecting = null;
        SemaphoreSlim? heldGate = null;
        try
        {
            var id = Guid.NewGuid();
            var request = Request(id, "SetUiPreferences", new { locale = "ru-RU", theme = "dark" });
            var accepted = await dispatcher.DispatchAsync(request, default);
            selecting = dispatcher.DispatchAsync(Request(Guid.NewGuid(), "SelectNativeFile", new { purpose = "volume" }), default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (holdStateGate)
            {
                // Model a worker paused in I/O while both admission and state are unavailable.
                heldGate = (SemaphoreSlim)typeof(BridgeDispatcher).GetField("gate",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(dispatcher)!;
                await heldGate.WaitAsync();
            }
            Assert.Equal(accepted, await dispatcher.DispatchAsync(request, default).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains("DUPLICATE_REQUEST_CONFLICT", await dispatcher.DispatchAsync(
                Request(id, "SetUiPreferences", new { locale = "ru-RU", theme = "light" }), default).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains("OPERATION_CONFLICT", await dispatcher.DispatchAsync(
                Request(Guid.NewGuid(), "SetUiPreferences", new { locale = "ru-RU", theme = "light" }), default).WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            heldGate?.Release();
            release.TrySetResult();
            if (selecting is not null) await selecting;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DuplicatePreferencesAreIdempotentAndStaleCommandsDoNotWrite()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../tests-dotnet", Guid.NewGuid().ToString("N")));
        var dispatcher = new BridgeDispatcher(root);
        var id = Guid.NewGuid();
        string Command(string theme, long revision, Guid? requestId = null) => JsonSerializer.Serialize(new
        {
            protocolVersion = 1, requestId = requestId ?? id, command = "SetUiPreferences",
            targetSessionId = (Guid?)null, expectedRevision = revision, payload = new { locale = "ru-RU", theme }
        }, Protocol.Json);
        try
        {
            var initial = await dispatcher.DispatchAsync(Command("dark", 0), default);
            var repeated = await dispatcher.DispatchAsync(Command("dark", 0), default);
            Assert.Equal(initial, repeated);
            var reordered = Command("dark", 0).Replace("\"locale\":\"ru-RU\",\"theme\":\"dark\"", "\"theme\":\"dark\",\"locale\":\"ru-RU\"");
            Assert.Equal(initial, await dispatcher.DispatchAsync(reordered, default));
            Assert.Contains("DUPLICATE_REQUEST_CONFLICT", await dispatcher.DispatchAsync(Command("light", 0), default));
            Assert.Contains("PLAN_STALE", await dispatcher.DispatchAsync(Command("light", 0, Guid.NewGuid()), default));
            var saved = await File.ReadAllTextAsync(Path.Combine(root, "ui.json"));
            Assert.Contains("dark", saved);
            Assert.DoesNotContain("light", saved);
            Assert.DoesNotContain("requestId", saved);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
