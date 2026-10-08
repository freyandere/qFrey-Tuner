using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using QFrey.Core.Contracts;
using QFrey.Desktop.Bridge;
using QFrey.Core.Localization;

namespace QFrey.Desktop;
public partial class MainWindow : Window
{
    private const string Origin = "https://qfrey.local";
    private readonly bool smoke;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim bridgeAdmission = new(16, 16);
    private readonly BridgeDispatcher bridge;
    private readonly string dataRoot;
    private readonly int? testPort;
    private bool mayClose;
    private bool closing;
    public MainWindow(bool smoke, int? testPort)
    {
        InitializeComponent();
        this.smoke = smoke;
        this.testPort = testPort;
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(MinWidth, Width);
        MinHeight = Math.Min(MinHeight, Height);
        dataRoot = smoke || testPort.HasValue
            ? Path.Combine(Environment.CurrentDirectory, ".cache", "tests", "webview", Guid.NewGuid().ToString("N"))
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "qFrey-Tuner");
        bridge = new BridgeDispatcher(dataRoot, localMetrics: endpoint => new Platform.WindowsProcessMetrics(endpoint).Sample,
            selectFile: (purpose, locale, token) => Platform.NativeFilePicker.SelectAsync(this, purpose, locale, token));
        bridge.SnapshotPublished += message =>
        {
            if (lifetime.IsCancellationRequested) return Task.CompletedTask;
            return Dispatcher.InvokeAsync(() => { if (!lifetime.IsCancellationRequested) Web.CoreWebView2?.PostWebMessageAsJson(message); }).Task.WaitAsync(lifetime.Token);
        };
        Loaded += async (_, _) => await InitializeAsync();
        Closing += async (_, args) =>
        {
            if (mayClose || smoke) return;
            args.Cancel = true;
            if (closing) return;
            closing = true;
            try
            {
                if (await bridge.ShutdownAsync()) { mayClose = true; Close(); }
                else
                {
                    MessageCatalog.TryGet("errors.operationFinishing", bridge.CurrentLocale, out var message);
                    MessageBox.Show(this, message, "qFrey-Tuner", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            finally { closing = false; }
        };
        // Async WebView callbacks can still observe Token after Closed; cancel before disposing WebView.
        Closed += (_, _) => { lifetime.Cancel(); bridge.Dispose(); Web.Dispose(); };
    }

    private async Task InitializeAsync()
    {
        try
        {
            var assets = await Task.Run(() => ExtractAssets(dataRoot), lifetime.Token);
            if (lifetime.IsCancellationRequested) return;
            // Explicit environment options; no production debugging port or host objects.
            var options = testPort is int port
                ? new CoreWebView2EnvironmentOptions
                {
                    AdditionalBrowserArguments = $"--remote-debugging-port={port} --remote-debugging-address=127.0.0.1"
                }
                : null;
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(dataRoot, "webview"), options)
                .WaitAsync(TimeSpan.FromSeconds(30), lifetime.Token);
            if (lifetime.IsCancellationRequested) return;
            await Web.EnsureCoreWebView2Async(environment).WaitAsync(TimeSpan.FromSeconds(30), lifetime.Token);
            if (lifetime.IsCancellationRequested) return;
            var core = Web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.SetVirtualHostNameToFolderMapping("qfrey.local", assets, CoreWebView2HostResourceAccessKind.DenyCors);
            core.NavigationStarting += (_, args) => args.Cancel = !IsLocalPage(args.Uri);
            core.FrameNavigationStarting += (_, args) => args.Cancel = true;
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, args) => args.Cancel = true;
            core.WindowCloseRequested += (_, _) => Close();
            core.WebMessageReceived += async (_, args) =>
            {
                if (!IsLocalPage(args.Source)) return;
                if (lifetime.IsCancellationRequested || !await bridgeAdmission.WaitAsync(0)) return;
                try
                {
                    var json = args.WebMessageAsJson;
                    var reply = await Task.Run(() => bridge.DispatchAsync(json, lifetime.Token), lifetime.Token);
                    if (!lifetime.IsCancellationRequested) core.PostWebMessageAsJson(reply);
                }
                catch (OperationCanceledException) { }
                finally { bridgeAdmission.Release(); }
            };
            core.Navigate(Origin + "/index.html");
            if (smoke)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                await bridge.Initialized.Task.WaitAsync(timeout.Token);
                while (!timeout.IsCancellationRequested)
                {
                    var ready = await core.ExecuteScriptAsync("Boolean(document.querySelector('main') && document.querySelector('.brand small')?.textContent)");
                    if (ready == "true") { Application.Current.Shutdown(0); return; }
                    await Task.Delay(50, timeout.Token);
                }
                Application.Current.Shutdown(4);
            }
        }
        catch (OperationCanceledException) { if (smoke) Application.Current.Shutdown(4); }
        catch (Exception error)
        {
            if (lifetime.IsCancellationRequested) return;
            if (smoke)
            {
                try
                {
                    Directory.CreateDirectory(dataRoot);
                    File.WriteAllText(Path.Combine(dataRoot, "startup-error.txt"),
                        $"{error.GetType().FullName} (0x{error.HResult:X8}){Environment.NewLine}{error.StackTrace}");
                }
                catch (Exception) { }
            }
            if (!smoke && !testPort.HasValue) MessageBox.Show(System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru"
                ? "Не удалось открыть интерфейс. Проверьте WebView2 и доступ к папке данных."
                : "The interface could not be opened. Check WebView2 and data directory access.", "qFrey-Tuner");
            Application.Current.Shutdown(3);
        }
    }

    private static bool IsLocalPage(string uri) => BridgeDispatcher.IsAllowedSource(uri);

    private static string ExtractAssets(string root)
    {
        var assembly = typeof(App).Assembly;
        var names = assembly.GetManifestResourceNames().Where(n => n.StartsWith("web/", StringComparison.Ordinal)).Order().ToArray();
        if (!names.Contains("web/index.html")) throw new InvalidDataException("Missing embedded frontend");
        var contents = names.Select(name =>
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return (Name: name[4..].Replace('\\', '/'), Bytes: memory.ToArray());
        }).ToArray();
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var item in contents) { digest.AppendData(Encoding.UTF8.GetBytes(item.Name)); digest.AppendData(item.Bytes); }
        var destination = Path.Combine(root, "assets", Convert.ToHexString(digest.GetHashAndReset()));
        if (Directory.Exists(destination))
        {
            foreach (var item in contents)
                if (!File.ReadAllBytes(Path.Combine(destination, item.Name)).AsSpan().SequenceEqual(item.Bytes)) throw new InvalidDataException("Asset integrity");
            return destination;
        }
        var temporary = destination + "." + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(temporary);
        foreach (var item in contents)
        {
            var file = Path.GetFullPath(Path.Combine(temporary, item.Name));
            if (!file.StartsWith(temporary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, item.Bytes);
        }
        try { Directory.Move(temporary, destination); }
        catch (IOException) when (Directory.Exists(destination)) { return ExtractAssets(root); }
        return destination;
    }
}
