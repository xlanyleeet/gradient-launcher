using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Gradient;

public sealed partial class MainWindow
{
    public sealed record DiscoverHit(string id, string name, string description, string author);
    public sealed record DiscoverVersion(string id, string name, string channel)
    {
        public string Label => $"{name} · {channel}";
    }

    private sealed record SearchPage(DiscoverHit[] hits, uint? next_offset);
    private sealed record SearchSpec(string Query, string Kind, string? Profile);
    private SearchSpec? discoverSearch;
    private uint? discoverOffset;

    private void OpenDiscover()
    {
        SettingsPage.Visibility = Visibility.Collapsed;
        LibraryPage.Visibility = Visibility.Collapsed;
        DiscoverPage.Visibility = Visibility.Visible;
        Navigation.SelectedItem = DiscoverNavigation;
        DiscoverQuery.Focus(FocusState.Programmatic);
    }

    private void CloseDiscover(object sender, RoutedEventArgs e)
    {
        ShowLibrary();
    }

    private Task DiscoverAction(Func<Task> action) => Run(async () =>
    {
        try { await action(); }
        catch (Exception error) { DiscoverStatus.Text = error.Message; }
    });

    private async void DiscoverSearch(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs e) =>
        await DiscoverAction(async () =>
        {
            var kind = (DiscoverKind.SelectedItem as ComboBoxItem)?.Tag as string ?? "modpack";
            var profile = kind == "mod" ? SelectedProfile?.id : null;
            if (kind == "mod" && profile is null)
            {
                DiscoverStatus.Text = "Спочатку виберіть профіль у бібліотеці.";
                return;
            }
            await SearchDiscover(new SearchSpec(e.QueryText, kind, profile), 0);
        });

    private async void DiscoverNext(object sender, RoutedEventArgs e)
    {
        if (discoverSearch is { } search && discoverOffset is uint offset)
            await DiscoverAction(() => SearchDiscover(search, offset));
    }

    private async Task SearchDiscover(SearchSpec search, uint offset)
    {
        DiscoverStatus.Text = "Шукаємо…";
        var page = await core.Call<SearchPage>(new
        {
            method = "discover", action = "search", query = search.Query,
            kind = search.Kind, profile = search.Profile, offset = offset.ToString(System.Globalization.CultureInfo.InvariantCulture)
        });
        discoverSearch = search;
        discoverOffset = page.next_offset;
        DiscoverResults.ItemsSource = page.hits;
        DiscoverMore.IsEnabled = discoverOffset.HasValue;
        DiscoverStatus.Text = page.hits.Length == 0 ? "Нічого не знайдено." : "";
    }

    private async void SelectDiscoverResult(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not DiscoverHit hit || discoverSearch is not { } search) return;
        await DiscoverAction(async () =>
        {
            DiscoverStatus.Text = "Завантажуємо версії…";
            var versions = await core.Call<DiscoverVersion[]>(new
            {
                method = "discover", action = "versions", project = hit.id, kind = search.Kind, profile = search.Profile
            });
            var picker = new ComboBox { Header = "Версія", ItemsSource = versions, DisplayMemberPath = "Label",
                SelectedIndex = versions.Length == 0 ? -1 : 0 };
            var content = new StackPanel { Spacing = 10 };
            content.Children.Add(new TextBlock { Text = hit.description, TextWrapping = TextWrapping.Wrap });
            content.Children.Add(versions.Length == 0
                ? new TextBlock { Text = "Сумісних версій немає." }
                : picker);
            var dialog = Editor(hit.name, content, "Встановити");
            dialog.IsPrimaryButtonEnabled = versions.Length > 0;
            DiscoverStatus.Text = "";
            if (await dialog.ShowAsync() != ContentDialogResult.Primary ||
                picker.SelectedItem is not DiscoverVersion version) return;
            DiscoverStatus.Text = "Встановлюємо…";
            await core.Call<JsonElement>(new
            {
                method = "discover", action = "install", project = hit.id, version = version.id,
                kind = search.Kind, profile = search.Profile
            });
            loadedTab = null;
            DiscoverStatus.Text = search.Kind == "modpack" ? "Модпак додано до черги." : "Мод встановлено.";
        });
    }
}
