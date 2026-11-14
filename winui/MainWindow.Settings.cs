using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Gradient;

public sealed partial class MainWindow
{
    private sealed record LaunchSettings(uint memory_mib, bool fullscreen, ushort width, ushort height, string[] arguments);
    private sealed record ProfileLaunchSettings(string? memory, string? java);

    private async void EditProfileLaunchSettings(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (SelectedProfile is not { running: false } profile) return;
        var values = await core.Call<ProfileLaunchSettings>(new { method = "profile_launch_settings", id = profile.id });
        var memory = new TextBox { Header = "Пам’ять (МіБ)", Text = values.memory ?? "", PlaceholderText = "З налаштувань лаунчера" };
        var java = new TextBox { Header = "Шлях до Java", Text = values.java ?? "", PlaceholderText = "Автоматично" };
        var form = new StackPanel { Spacing = 12 };
        form.Children.Add(memory);
        form.Children.Add(java);
        if (await Editor("Java та пам’ять профілю", form, "Зберегти").ShowAsync() == ContentDialogResult.Primary)
            await ApplyProfileChange(new { method = "save_profile_launch_settings", id = profile.id,
                memory = memory.Text, java = java.Text }, "Налаштування профілю збережено.");
    });

    private async void OpenSettings(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var values = await core.Call<LaunchSettings>(new { method = "launch_settings" });
        MemorySetting.Text = values.memory_mib.ToString();
        FullscreenSetting.IsOn = values.fullscreen;
        WidthSetting.Text = values.width.ToString();
        HeightSetting.Text = values.height.ToString();
        ArgumentsSetting.Text = string.Join(Environment.NewLine, values.arguments);
        SaveSettingsButton.IsEnabled = true;
        SettingsStatus.Text = "";
        LibraryPage.Visibility = Visibility.Collapsed;
        DiscoverPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Visible;
        MemorySetting.Focus(FocusState.Programmatic);
    });

    private void CloseSettings(object sender, RoutedEventArgs e)
    {
        if (!SaveSettingsButton.IsEnabled) return;
        SettingsPage.Visibility = Visibility.Collapsed;
        LibraryPage.Visibility = Visibility.Visible;
    }

    private async void SaveSettings(object sender, RoutedEventArgs e)
    {
        if (!uint.TryParse(MemorySetting.Text, out var memory) || memory == 0 ||
            !ushort.TryParse(WidthSetting.Text, out var width) || width == 0 ||
            !ushort.TryParse(HeightSetting.Text, out var height) || height == 0)
        {
            SettingsStatus.Text = "Введіть додатні цілі значення пам’яті та роздільної здатності.";
            return;
        }

        await Run(async () =>
        {
            SaveSettingsButton.IsEnabled = false;
            try
            {
                var arguments = ArgumentsSetting.Text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                var values = new LaunchSettings(memory, FullscreenSetting.IsOn, width, height, arguments);
                await core.Call<JsonElement>(new { method = "save_launch_settings", values });
                SettingsStatus.Text = "Налаштування збережено.";
            }
            catch (Exception error) { SettingsStatus.Text = error.Message; }
            finally { SaveSettingsButton.IsEnabled = true; }
        });
    }
}
