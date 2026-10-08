using System.Windows;
using Microsoft.Win32;
using QFrey.Core.Contracts;
using QFrey.Core.Localization;

namespace QFrey.Desktop.Platform;

public static class NativeFilePicker
{
    public static async Task<string?> SelectAsync(Window owner, string purpose, Locale locale, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return await owner.Dispatcher.InvokeAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            string Text(string key) => MessageCatalog.TryGet(key, locale, out var message) ? message
                : throw new InvalidOperationException("LOCALIZATION_KEY_MISSING");
            if (purpose == "volume")
            {
                var folder = new OpenFolderDialog { Title = Text("native.chooseVolume"), Multiselect = false };
                return folder.ShowDialog(owner) == true ? folder.FolderName : null;
            }
            if (purpose == "restore")
            {
                var open = new OpenFileDialog { Title = Text("native.chooseBackup"),
                    Filter = Text("native.backupFilter"),
                    CheckFileExists = true, Multiselect = false };
                return open.ShowDialog(owner) == true ? open.FileName : null;
            }
            if (purpose is not ("exportJson" or "exportHtml")) throw new ArgumentException("INVALID_SELECTION_PURPOSE");
            var html = purpose == "exportHtml";
            var save = new SaveFileDialog { Title = Text("native.saveReport"), AddExtension = true,
                DefaultExt = html ? ".html" : ".json", FileName = html ? "qFrey-report.html" : "qFrey-report.json",
                Filter = html ? "HTML (*.html)|*.html" : "JSON (*.json)|*.json", OverwritePrompt = true, CheckPathExists = true };
            return save.ShowDialog(owner) == true ? save.FileName : null;
        }).Task.WaitAsync(token).ConfigureAwait(false);
    }
}
