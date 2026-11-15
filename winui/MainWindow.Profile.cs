using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

using static Gradient.InterfaceText;

namespace Gradient;

public sealed partial class MainWindow
{
    private sealed record ContentEntry(string path, string name, string file, string? version,
        bool enabled, bool locked, bool synced, string kind, bool has_update, bool can_choose_version);
    private sealed record InstalledModVersions(string? current_id, DiscoverVersion[] options);
    private sealed record WorldEntry(string id, string name, string? last_played);
    private string? loadedTab;

    private async void DetailChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ready) await Run(LoadSelected);
    }

    private async Task LoadProfileTab(Profile profile)
    {
        bool content = Equals(Details.SelectedItem, ContentTab);
        var panel = content ? ContentRows : WorldRows;
        AddModButton.IsEnabled = !profile.running && !profile.quarantined;
        string key = $"{profile.id}/{profile.running}/{profile.quarantined}/{content}";
        if (loadedTab == key && panel.Children.Count > 0) return;

        panel.Children.Clear();
        panel.Children.Add(new TextBlock { Text = Tr("Завантажуємо…") });
        try
        {
            if (content)
            {
                var entries = await core.Call<ContentEntry[]>(new { method = "content", id = profile.id });
                if (SelectedProfile?.id != profile.id || !Equals(Details.SelectedItem, ContentTab)) return;
                panel.Children.Clear();
                foreach (var entry in entries) panel.Children.Add(ContentRow(profile, entry));
            }
            else
            {
                var worlds = await core.Call<WorldEntry[]>(new { method = "worlds", id = profile.id });
                if (SelectedProfile?.id != profile.id || !Equals(Details.SelectedItem, WorldsTab)) return;
                panel.Children.Clear();
                foreach (var world in worlds) panel.Children.Add(WorldRow(profile, world));
            }

            if (panel.Children.Count == 0)
                panel.Children.Add(new TextBlock { Text = content ? Tr("Контенту ще немає.") : Tr("Світів ще немає.") });
            loadedTab = key;
        }
        catch
        {
            panel.Children.Clear();
            loadedTab = null;
            throw;
        }
    }

    private StackPanel ContentRow(Profile profile, ContentEntry entry)
    {
        string state = entry.enabled ? Tr("Увімкнено") : Tr("Вимкнено");
        if (entry.locked || entry.synced) state += Tr(" · Керований");
        var row = ProfileRow(entry.name, $"{entry.file} · {entry.kind} · {entry.version ?? "—"} · {state}");
        if (profile.running || profile.quarantined || entry.locked || entry.synced) return row;

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var toggle = new Button { Content = entry.enabled ? Tr("Вимкнути") : Tr("Увімкнути") };
        toggle.Click += async (_, _) => await MutateProfile(profile, new
        {
            method = "set_content_enabled", id = profile.id, path = entry.path,
            action = entry.enabled ? "disable" : "enable"
        });
        var remove = new Button { Content = Tr("Видалити") };
        remove.Click += async (_, _) => await Run(async () =>
        {
            if (SelectedProfile?.id != profile.id) return;
            var dialog = new ContentDialog
            {
                XamlRoot = Layout.XamlRoot, Title = Tr("Видалити контент?"),
                Content = entry.name, PrimaryButtonText = Tr("Видалити"), CloseButtonText = Tr("Скасувати")
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                await ApplyProfileChange(new { method = "remove_content", id = profile.id, path = entry.path });
        });
        actions.Children.Add(toggle);
        if (entry.has_update)
        {
            var update = new Button { Content = Tr("Оновити") };
            update.Click += async (_, _) => await MutateProfile(profile,
                new { method = "update_content", id = profile.id, path = entry.path });
            actions.Children.Add(update);
        }
        if (entry.can_choose_version)
        {
            var version = new Button { Content = Tr("Змінити версію") };
            version.Click += async (_, _) => await ChangeModVersion(profile, entry);
            actions.Children.Add(version);
        }
        actions.Children.Add(remove);
        row.Children.Add(actions);
        return row;
    }

    private Task ChangeModVersion(Profile profile, ContentEntry entry) => Run(async () =>
    {
        if (SelectedProfile?.id != profile.id) return;
        var versions = await core.Call<InstalledModVersions>(new
        {
            method = "installed_mod_versions", id = profile.id, path = entry.path
        });
        var picker = new ComboBox { Header = Tr("Версія"), ItemsSource = versions.options, DisplayMemberPath = "Label" };
        picker.SelectedItem = versions.options.FirstOrDefault(option => option.id == versions.current_id)
            ?? versions.options.FirstOrDefault();
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock { Text = Tr("Встановлено · {0}", entry.version ?? "—") });
        if (versions.options.Length > 0)
            content.Children.Add(picker);
        else
            content.Children.Add(new TextBlock { Text = Tr("Сумісних версій немає.") });
        var dialog = Editor(entry.name, content, Tr("Змінити версію"));
        void Validate() => dialog.IsPrimaryButtonEnabled =
            picker.SelectedItem is DiscoverVersion version && version.id != versions.current_id;
        picker.SelectionChanged += (_, _) => Validate();
        Validate();
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && picker.SelectedItem is DiscoverVersion selected)
            await ApplyProfileChange(new { method = "switch_mod_version", id = profile.id, path = entry.path,
                version = selected.id }, Tr("Встановлено версію {0}.", selected.name));
    }, waitForCore: true);

    private StackPanel WorldRow(Profile profile, WorldEntry world)
    {
        var row = ProfileRow(world.name, Tr("Остання гра · {0}", world.last_played ?? "—"));
        if (profile.running || profile.quarantined) return row;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var play = new Button { Content = Tr("Відкрити світ") };
        play.Click += async (_, _) => await MutateProfile(profile, new { method = "open_world", id = profile.id, world = world.id });
        var backup = new Button { Content = Tr("Резервна копія") };
        backup.Click += async (_, _) => await MutateProfile(profile,
            new { method = "backup_world", id = profile.id, world = world.id },
            Tr("Резервну копію створено."));
        actions.Children.Add(play);
        actions.Children.Add(backup);
        row.Children.Add(actions);
        return row;
    }

    private static StackPanel ProfileRow(string title, string subtitle)
    {
        var row = new StackPanel { Spacing = 5, Margin = new Thickness(0, 0, 0, 8) };
        row.Children.Add(new TextBlock { Text = title, FontSize = 17, TextWrapping = TextWrapping.Wrap });
        row.Children.Add(new TextBlock { Text = subtitle, TextWrapping = TextWrapping.Wrap });
        return row;
    }

    private async void AddMod(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (SelectedProfile is not { } profile) return;
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".jar");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is not null && SelectedProfile?.id == profile.id)
            await ApplyProfileChange(new { method = "add_mod", id = profile.id, path = file.Path });
    });

    private Task MutateProfile(Profile profile, object request, string message = "") => Run(async () =>
    {
        if (SelectedProfile?.id == profile.id)
            await ApplyProfileChange(request, message);
    });

    private async Task ApplyProfileChange(object request, string message = "")
    {
        await core.Call<JsonElement>(request);
        loadedTab = null;
        Status.Text = message;
        await Update();
    }
}
