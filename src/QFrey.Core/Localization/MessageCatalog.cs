using System.Text.Json;
using QFrey.Core.Contracts;

namespace QFrey.Core.Localization;

// The frontend RU/EN catalog is embedded at build time; reports do not need Node or a browser.
public static class MessageCatalog
{
    private static readonly Lazy<JsonDocument> resource = new(() =>
    {
        using var stream = typeof(MessageCatalog).Assembly.GetManifestResourceStream("QFrey.Locales.json")
            ?? throw new InvalidOperationException("Localization resource missing.");
        return JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 8 });
    });
    public static bool TryGet(string key, Locale locale, out string message)
    {
        var catalog = resource.Value.RootElement.GetProperty("messages").GetProperty(LocaleName(locale));
        if (catalog.TryGetProperty(key, out var value)) { message = value.GetString()!; return true; }
        message = string.Empty; return false;
    }
    public static string LocalizeParameter(string key, string value, Locale locale)
    {
        var parameters = resource.Value.RootElement.GetProperty("parameters").GetProperty(LocaleName(locale));
        return parameters.TryGetProperty(key, out var values) && values.TryGetProperty(value, out var translated)
            ? translated.GetString()! : value;
    }
    private static string LocaleName(Locale locale) => locale switch
    {
        Locale.RuRu => "ru-RU", Locale.EnUs => "en-US", _ => throw new ArgumentOutOfRangeException(nameof(locale))
    };
}
