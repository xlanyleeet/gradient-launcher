using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Gradient;

public sealed partial class MainWindow
{
    public sealed record ExportCandidate(string path, bool disabled, bool defaultSelected);

    private async void ExportProfile(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (SelectedProfile is not { running: false } profile) return;
        var candidates = await core.Call<ExportCandidate[]>(new { method = "export_candidates", id = profile.id });
        var rows = new StackPanel { Spacing = 6 };
        foreach (var candidate in candidates)
            rows.Children.Add(new CheckBox { Content = candidate.path, Tag = candidate.path,
                IsEnabled = !candidate.disabled, IsChecked = !candidate.disabled && candidate.defaultSelected });
        var dialog = Editor("Виберіть вміст модпаку", new ScrollViewer { Content = rows, MaxHeight = 380 }, "Продовжити");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var included = rows.Children.OfType<CheckBox>().Where(item => item.IsChecked == true)
            .Select(item => (string)item.Tag).ToArray();

        var picker = new FileSavePicker { SuggestedFileName = "modpack" };
        picker.FileTypeChoices.Add("Модпак Modrinth", new[] { ".mrpack" });
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        await core.Call<JsonElement>(new { method = "export_profile", id = profile.id, path = file.Path, included });
        Status.Text = "Модпак експортовано.";
    });

    private async void ImportLauncher(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var launchers = new[] { "PrismLauncher", "MultiMC", "ATLauncher", "GDLauncher", "Curseforge" };
        var select = new ComboBox { Header = "Лаунчер", ItemsSource = launchers, SelectedIndex = 0 };
        if (await Editor("Імпорт з іншого лаунчера", select, "Вибрати папку").ShowAsync() != ContentDialogResult.Primary)
            return;
        var launcher = (string)select.SelectedItem;
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var source = await picker.PickSingleFolderAsync();
        if (source is null) return;
        var folders = await core.Call<string[]>(new { method = "import_sources", launcher, @base = source.Path });
        if (folders.Length == 0)
        {
            Status.Text = "У цій папці немає профілів для імпорту.";
            return;
        }
        var profile = new ComboBox { Header = "Профіль", ItemsSource = folders, SelectedIndex = 0 };
        if (await Editor("Виберіть профіль", profile, "Імпортувати").ShowAsync() != ContentDialogResult.Primary)
            return;
        await ApplyProfileChange(new { method = "import_profile", launcher, @base = source.Path, folder = (string)profile.SelectedItem },
            "Імпорт додано до черги.");
    });
}
