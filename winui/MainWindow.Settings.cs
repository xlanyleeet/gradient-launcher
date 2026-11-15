using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Gradient;

public sealed partial class MainWindow
{
    private sealed record LaunchSettings(uint memory_mib, bool fullscreen, ushort width, ushort height, string[] arguments);
    private sealed record ProfileLaunchSettings(string? memory, string? java);
    private sealed record JavaRuntime(uint parsed_version, string version, string path);

    private void RegisterProjectLinks(object sender, RoutedEventArgs e)
    {
        try
        {
            Microsoft.Windows.AppLifecycle.ActivationRegistrationManager.RegisterForProtocolActivation(
                "gradient-native", "", "Gradient Launcher", "");
            Microsoft.Windows.AppLifecycle.ActivationRegistrationManager.RegisterForFileTypeActivation(
                new[] { ".mrpack" }, "", "Gradient Launcher", Array.Empty<string>(), "");
            SettingsStatus.Text = "Gradient зареєстровано для посилань gradient-native:// та файлів .mrpack.";
        }
        catch (Exception error) { SettingsStatus.Text = error.Message; }
    }

    private async void ManageJava(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        try
        {
            var runtimes = await core.Call<JavaRuntime[]>(new { method = "java_runtimes" });
            var versions = new ComboBox { Header = "Версія Java", ItemsSource = new[] { "8", "17", "21", "25" }, SelectedIndex = 2 };
            var current = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            void ShowRuntime()
            {
                var path = runtimes.FirstOrDefault(runtime => runtime.parsed_version.ToString() == (string)versions.SelectedItem);
                current.Text = path is null ? "Для цієї версії шлях ще не збережено." : $"{path.version} · {path.path}";
            }
            versions.SelectionChanged += (_, _) => ShowRuntime();
            ShowRuntime();
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(versions);
            content.Children.Add(current);
            var dialog = Editor("Інсталяції Java", content, "Вибрати файл");
            dialog.SecondaryButtonText = "Завантажити";
            var result = await dialog.ShowAsync();
            var major = (string)versions.SelectedItem;
            if (result == ContentDialogResult.Primary)
            {
                var picker = new FileOpenPicker();
                picker.FileTypeFilter.Add(".exe");
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
                var file = await picker.PickSingleFileAsync();
                if (file is null) return;
                await core.Call<JsonElement>(new { method = "set_java_runtime", major, path = file.Path });
            }
            else if (result == ContentDialogResult.Secondary)
            {
                JavaStatus.Text = $"Завантажуємо Java {major}…";
                await core.Call<JsonElement>(new { method = "download_java_runtime", major });
            }
            else return;
            JavaStatus.Text = $"Java {major} збережено.";
        }
        catch (Exception error) { JavaStatus.Text = error.Message; }
    }, waitForCore: true);

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

    private Task OpenSettings() => Run(async () =>
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
        Navigation.SelectedItem = SettingsNavigation;
        Navigation.IsPaneOpen = false;
        MemorySetting.Focus(FocusState.Programmatic);
    }, waitForCore: true);

    private void CloseSettings(object sender, RoutedEventArgs e)
    {
        if (!SaveSettingsButton.IsEnabled) return;
        ShowLibrary();
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
