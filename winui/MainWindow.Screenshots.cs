using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Gradient;

public sealed partial class MainWindow
{
    public sealed record Screenshot(string filename, string path);

    private string? screenshotProfile;

    private void ClearScreenshots(string message)
    {
        ScreenshotPicker.ItemsSource = null;
        ScreenshotPicker.IsEnabled = false;
        ExportShotsButton.IsEnabled = false;
        ScreenshotPreview.Source = null;
        ScreenshotStatus.Text = message;
        screenshotProfile = null;
    }

    private async Task LoadScreenshots(Profile profile)
    {
        var key = $"{profile.id}/{profile.running}";
        if (screenshotProfile == key) return;

        ClearScreenshots("Завантажуємо скриншоти…");
        try
        {
            var rows = await core.Call<Screenshot[]>(new { method = "screenshots", id = profile.id });
            if (SelectedProfile?.id != profile.id || !Equals(Details.SelectedItem, ScreenshotsTab)) return;
            ScreenshotPicker.ItemsSource = rows;
            ScreenshotPicker.IsEnabled = rows.Length > 0;
            ExportShotsButton.IsEnabled = rows.Length > 0;
            ScreenshotStatus.Text = rows.Length == 0 ? "Скриншотів ще немає." : "";
            if (rows.Length > 0) ScreenshotPicker.SelectedIndex = 0;
            screenshotProfile = key;
        }
        catch
        {
            ScreenshotStatus.Text = "Не вдалося завантажити скриншоти.";
            throw;
        }
    }

    private void ScreenshotChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || ScreenshotPicker.SelectedItem is not Screenshot shot) return;
        if (!File.Exists(shot.path))
        {
            ScreenshotPreview.Source = null;
            ScreenshotStatus.Text = "Файл скриншота більше не доступний.";
            return;
        }
        ScreenshotPreview.Source = new BitmapImage(new Uri(shot.path)) { DecodePixelWidth = 1400 };
        ScreenshotStatus.Text = "";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ScreenshotPreview, shot.filename);
    }

    private async void ExportScreenshots(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (SelectedProfile is not { } profile) return;
        var picker = new FileSavePicker { SuggestedFileName = "screenshots" };
        picker.FileTypeChoices.Add("ZIP", new[] { ".zip" });
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        await core.Call<JsonElement>(new { method = "export_screenshots", id = profile.id, path = file.Path });
        ScreenshotStatus.Text = "Архів збережено.";
    });
}
