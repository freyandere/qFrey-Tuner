using System.IO;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace QFrey.Desktop;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var smoke = e.Args.Contains("--smoke-test");
        var check = e.Args.Contains("--check");
        var testPortArguments = e.Args.Where(argument => argument.StartsWith("--webview-test-port", StringComparison.Ordinal)).ToArray();
        int? testPort = null;
        if (testPortArguments.Length > 1 || testPortArguments.Length == 1)
        {
            var argument = testPortArguments.Length == 1 ? testPortArguments[0] : null;
            var value = argument?.StartsWith("--webview-test-port=", StringComparison.Ordinal) == true
                ? argument["--webview-test-port=".Length..] : "";
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1024 or > 65535)
            {
                Shutdown(5);
                return;
            }
            try
            {
                using var probe = new TcpListener(IPAddress.Loopback, port);
                probe.Start();
            }
            catch (SocketException)
            {
                Shutdown(6);
                return;
            }
            testPort = port;
        }
        try
        {
            _ = CoreWebView2Environment.GetAvailableBrowserVersionString();
            if (check)
            {
                if (!typeof(App).Assembly.GetManifestResourceNames().Contains("web/index.html")) throw new InvalidOperationException();
                Shutdown(0);
                return;
            }
            MainWindow = new MainWindow(smoke, testPort);
            MainWindow.Show();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            if (!smoke && !check && !testPort.HasValue) MessageBox.Show(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru"
                ? "Для запуска нужен Microsoft Edge WebView2 Runtime. Установите Evergreen Runtime с сайта Microsoft."
                : "Microsoft Edge WebView2 Runtime is required. Install Evergreen Runtime from Microsoft.", "qFrey-Tuner", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
        }
        catch (Exception error)
        {
            if (smoke)
            {
                try
                {
                    var path = Path.Combine(Environment.CurrentDirectory, ".cache", "tests", "webview");
                    Directory.CreateDirectory(path);
                    File.WriteAllText(Path.Combine(path, "app-startup-error.txt"),
                        $"{error.GetType().FullName} (0x{error.HResult:X8}){Environment.NewLine}{error.StackTrace}");
                }
                catch (Exception) { }
            }
            if (!smoke && !check && !testPort.HasValue) MessageBox.Show(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru"
                ? "Не удалось запустить приложение. Проверьте доступ к папке данных."
                : "The application could not start. Check access to the data directory.", "qFrey-Tuner", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(3);
        }
    }
}
