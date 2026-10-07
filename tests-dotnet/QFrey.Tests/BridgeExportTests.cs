using System.Text.Json;
using QFrey.Core.Contracts;
using QFrey.Core.Persistence;
using QFrey.Desktop.Bridge;
using Xunit;

namespace QFrey.Tests;

public sealed class BridgeExportTests
{
    [Fact]
    public async Task OfflineJsonExportUsesNativeTokenAndCachesPickerAndExportRequests()
    {
        var root = NewRoot();
        var destinationDirectory = Path.Combine(root, "selected");
        Directory.CreateDirectory(destinationDirectory);
        var destinationPath = Path.Combine(destinationDirectory, "cycle.json");
        var callbackCalls = 0;
        var selectedPurpose = "";
        var selectedLocale = Locale.EnUs;
        using var bridge = new BridgeDispatcher(root, selectFile: (purpose, locale, _) =>
        {
            callbackCalls++;
            selectedPurpose = purpose;
            selectedLocale = locale;
            return Task.FromResult<string?>(destinationPath);
        });

        try
        {
            Assert.True(ReadReply(await bridge.DispatchAsync(Request("Initialize", new InitializePayload(Protocol.Version)), default)).Ok);
            var preferenceReply = ReadReply(await bridge.DispatchAsync(Request("SetUiPreferences",
                new SetUiPreferencesPayload("ru-RU", ThemePreference.Dark), revision: 0), default));
            Assert.True(preferenceReply.Ok);

            var cycle = CycleStoreTests.Record(Guid.NewGuid(), new("http://127.0.0.1:8080", "5.1.0", "2.11.0", "2.0.11"));
            await new CycleStore(Path.Combine(root, "cycles")).SaveAsync(cycle);

            var selectId = Guid.NewGuid();
            var selectRequest = Request("SelectNativeFile", new SelectFilePayload("exportJson"), selectId);
            var selectedJson = await bridge.DispatchAsync(selectRequest, default);
            var selection = JsonSerializer.Deserialize<CommandReply<NativeSelection?>>(selectedJson, Protocol.Json)!;
            Assert.True(selection.Ok);
            Assert.NotNull(selection.Data);
            Assert.Equal("exportJson", selection.Data!.Purpose);
            Assert.Equal("cycle.json", selection.Data.DisplayName);
            Assert.Equal(Locale.RuRu, selectedLocale);
            Assert.Equal("exportJson", selectedPurpose);
            Assert.DoesNotContain(destinationPath, selectedJson, StringComparison.Ordinal);
            Assert.DoesNotContain("path", selectedJson, StringComparison.OrdinalIgnoreCase);

            var duplicateSelection = await bridge.DispatchAsync(selectRequest, default);
            Assert.Equal(selectedJson, duplicateSelection);
            Assert.Equal(1, callbackCalls);
            Assert.False(File.Exists(destinationPath));

            var exportId = Guid.NewGuid();
            var exportRequest = Request("ExportReport", new ExportReportPayload(cycle.CycleId, ReportFormat.Json,
                selection.Data.Token, Locale.RuRu), exportId);
            Assert.DoesNotContain(destinationPath, exportRequest, StringComparison.Ordinal);
            var exportReply = await bridge.DispatchAsync(exportRequest, default);
            Assert.True(ReadReply(exportReply).Ok);
            Assert.Equal(CycleReport.ToJson(cycle).Bytes, await File.ReadAllBytesAsync(destinationPath));
            Assert.DoesNotContain(destinationPath, exportReply, StringComparison.Ordinal);

            var duplicateExport = await bridge.DispatchAsync(exportRequest, default);
            Assert.Equal(exportReply, duplicateExport);
            Assert.Equal(1, callbackCalls);
        }
        finally
        {
            bridge.Dispose();
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public async Task WrongPurposeSelectionTokenDoesNotWriteAReport()
    {
        var root = NewRoot();
        var htmlPath = Path.Combine(root, "selected", "cycle.html");
        Directory.CreateDirectory(Path.GetDirectoryName(htmlPath)!);
        using var bridge = new BridgeDispatcher(root,
            selectFile: (_, _, _) => Task.FromResult<string?>(htmlPath));

        try
        {
            Assert.True(ReadReply(await bridge.DispatchAsync(Request("Initialize", new InitializePayload(Protocol.Version)), default)).Ok);
            var cycle = CycleStoreTests.Record(Guid.NewGuid(), new("http://127.0.0.1:8080", "5.1.0", "2.11.0", "2.0.11"));
            await new CycleStore(Path.Combine(root, "cycles")).SaveAsync(cycle);

            var selectionJson = await bridge.DispatchAsync(Request("SelectNativeFile", new SelectFilePayload("exportHtml")), default);
            var selection = JsonSerializer.Deserialize<CommandReply<NativeSelection?>>(selectionJson, Protocol.Json)!;
            Assert.True(selection.Ok);
            Assert.Equal("exportHtml", selection.Data!.Purpose);

            var exportJson = await bridge.DispatchAsync(Request("ExportReport", new ExportReportPayload(cycle.CycleId,
                ReportFormat.Json, selection.Data.Token, Locale.EnUs)), default);
            var failure = ReadReply(exportJson);
            Assert.False(failure.Ok);
            Assert.Equal(ErrorCodes.InvalidCommand, failure.Error!.Code);
            Assert.False(File.Exists(htmlPath));
            Assert.False(File.Exists(Path.ChangeExtension(htmlPath, ".json")));
        }
        finally
        {
            bridge.Dispose();
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public async Task CancelledNativePickerReturnsANullSelectionWithoutError()
    {
        var root = NewRoot();
        var pickerCalls = 0;
        using var bridge = new BridgeDispatcher(root, selectFile: (_, _, _) =>
        {
            pickerCalls++;
            return Task.FromResult<string?>(null);
        });

        try
        {
            Assert.True(ReadReply(await bridge.DispatchAsync(Request("Initialize", new InitializePayload(Protocol.Version)), default)).Ok);
            var request = Request("SelectNativeFile", new SelectFilePayload("exportJson"));
            var response = await bridge.DispatchAsync(request, default);
            var selection = JsonSerializer.Deserialize<CommandReply<NativeSelection?>>(response, Protocol.Json)!;
            Assert.True(selection.Ok);
            Assert.Null(selection.Data);
            Assert.Null(selection.Error);
            Assert.Equal(1, pickerCalls);

            Assert.Equal(response, await bridge.DispatchAsync(request, default));
            Assert.Equal(1, pickerCalls);
        }
        finally
        {
            bridge.Dispose();
            DeleteTestRoot(root);
        }
    }

    private static string Request(string command, object payload, Guid? requestId = null, long? revision = null) =>
        JsonSerializer.Serialize(new
        {
            protocolVersion = Protocol.Version,
            requestId = requestId ?? Guid.NewGuid(),
            command,
            targetSessionId = (Guid?)null,
            expectedRevision = revision,
            payload
        }, Protocol.Json);

    private static Reply ReadReply(string json) => JsonSerializer.Deserialize<Reply>(json, Protocol.Json)!;

    private static string NewRoot()
    {
        var root = Path.Combine(ProjectRoot(), ".cache", "tests", "bridge-export", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTestRoot(string root)
    {
        var fullPath = Path.GetFullPath(root);
        var parent = Path.GetFullPath(Path.Combine(ProjectRoot(), ".cache", "tests", "bridge-export"));
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
}
