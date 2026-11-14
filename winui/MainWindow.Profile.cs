using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Gradient;

public sealed partial class MainWindow
{
    private sealed record ContentEntry(string path, string name, string file, string? version,
        bool enabled, bool locked, bool synced, string kind, bool has_update);
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
        panel.Children.Add(new TextBlock { Text = "Завантажуємо…" });
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
                panel.Children.Add(new TextBlock { Text = content ? "Контенту ще немає." : "Світів ще немає." });
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
        string state = entry.enabled ? "Увімкнено" : "Вимкнено";
        if (entry.locked || entry.synced) state += " · Керований";
        var row = ProfileRow(entry.name, $"{entry.file} · {entry.kind} · {entry.version ?? "—"} · {state}");
        if (profile.running || profile.quarantined || entry.locked || entry.synced) return row;

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var toggle = new Button { Content = entry.enabled ? "Вимкнути" : "Увімкнути" };
        toggle.Click += async (_, _) => await MutateProfile(profile, new
        {
            method = "set_content_enabled", id = profile.id, path = entry.path,
            action = entry.enabled ? "disable" : "enable"
        });
        var remove = new Button { Content = "Видалити" };
        remove.Click += async (_, _) => await Run(async () =>
        {
            if (SelectedProfile?.id != profile.id) return;
            var dialog = new ContentDialog
            {
                XamlRoot = Layout.XamlRoot, Title = "Видалити контент?",
                Content = entry.name, PrimaryButtonText = "Видалити", CloseButtonText = "Скасувати"
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                await ApplyProfileChange(new { method = "remove_content", id = profile.id, path = entry.path });
        });
        actions.Children.Add(toggle);
        if (entry.has_update)
        {
            var update = new Button { Content = "Оновити" };
            update.Click += async (_, _) => await MutateProfile(profile,
                new { method = "update_content", id = profile.id, path = entry.path });
            actions.Children.Add(update);
        }
        actions.Children.Add(remove);
        row.Children.Add(actions);
        return row;
    }

    private StackPanel WorldRow(Profile profile, WorldEntry world)
    {
        var row = ProfileRow(world.name, $"Остання гра · {world.last_played ?? "—"}");
        if (profile.running || profile.quarantined) return row;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var play = new Button { Content = "Відкрити світ" };
        play.Click += async (_, _) => await MutateProfile(profile, new { method = "open_world", id = profile.id, world = world.id });
        var backup = new Button { Content = "Резервна копія" };
        backup.Click += async (_, _) => await MutateProfile(profile,
            new { method = "backup_world", id = profile.id, world = world.id },
            "Резервну копію створено.");
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
