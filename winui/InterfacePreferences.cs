using System.Globalization;
using System.Text.Json;
using Microsoft.UI.Xaml.Markup;

namespace Gradient;

internal sealed record InterfacePreferences(string Theme = "system", string Language = "uk")
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GradientNative", "interface.json");

    public static InterfacePreferences Load()
    {
        try { return JsonSerializer.Deserialize<InterfacePreferences>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
    }
}

internal static class InterfaceText
{
    public static readonly string Language = InterfacePreferences.Load().Language;
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo(Language == "en" ? "en" : "uk");
    private sealed record Catalog(Dictionary<string, string> English, Dictionary<string, string> Install);
    private static readonly Lazy<Catalog> Labels = new(() =>
    {
        try
        {
            return JsonSerializer.Deserialize<Catalog>(
                File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "InterfaceStrings.json")),
                new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? new([], []);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new([], []);
        }
    });

    public static string Tr(string key, params object?[] arguments)
    {
        var text = Language == "en" && Labels.Value.English.TryGetValue(key, out var translation) ? translation : key;
        return arguments.Length == 0 ? text : string.Format(Culture, text, arguments);
    }

    public static string InstallLabel(string key) => Labels.Value.Install.TryGetValue(key, out var label) ? Tr(label) : key;
}

[MarkupExtensionReturnType(ReturnType = typeof(string))]
public sealed class TranslateExtension : MarkupExtension
{
    public string Key { get; set; } = "";
    protected override object ProvideValue() => InterfaceText.Tr(Key);
}
