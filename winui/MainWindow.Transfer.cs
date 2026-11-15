using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

using static Gradient.InterfaceText;

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
        var dialog = Editor(Tr("Виберіть вміст модпаку"), new ScrollViewer { Content = rows, MaxHeight = 380 }, Tr("Продовжити"));
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var included = rows.Children.OfType<CheckBox>().Where(item => item.IsChecked == true)
            .Select(item => (string)item.Tag).ToArray();

        var picker = new FileSavePicker { SuggestedFileName = "modpack" };
        picker.FileTypeChoices.Add(Tr("Модпак Modrinth"), new[] { ".mrpack" });
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        await core.Call<JsonElement>(new { method = "export_profile", id = profile.id, path = file.Path, included });
        Status.Text = Tr("Модпак експортовано.");
    });

    private async void ImportLauncher(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var launchers = new[] { "PrismLauncher", "MultiMC", "ATLauncher", "GDLauncher", "Curseforge" };
        var select = new ComboBox { Header = Tr("Лаунчер"), ItemsSource = launchers, SelectedIndex = 0 };
        if (await Editor(Tr("Імпорт з іншого лаунчера"), select, Tr("Вибрати папку")).ShowAsync() != ContentDialogResult.Primary)
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
            Status.Text = Tr("У цій папці немає профілів для імпорту.");
            return;
        }
        var profile = new ComboBox { Header = Tr("Профіль"), ItemsSource = folders, SelectedIndex = 0 };
        if (await Editor(Tr("Виберіть профіль"), profile, Tr("Імпортувати")).ShowAsync() != ContentDialogResult.Primary)
            return;
        await ApplyProfileChange(new { method = "import_profile", launcher, @base = source.Path, folder = (string)profile.SelectedItem },
            Tr("Імпорт додано до черги."));
    });
}
