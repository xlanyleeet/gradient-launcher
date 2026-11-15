using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Gradient;

public sealed partial class MainWindow
{
    public sealed record DiscoverHit(string id, string name, string description, string author, string? icon)
    {
        public BitmapImage? IconSource => Uri.TryCreate(icon, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? new BitmapImage(uri) { DecodePixelWidth = 96 } : null;
    }
    public sealed record DiscoverVersion(string id, string name, string channel)
    {
        public string Label => $"{name} · {channel}";
    }

    private sealed record SearchPage(DiscoverHit[] hits, uint? next_offset);
    private sealed record SearchSpec(string Query, string Kind, string? Profile);
    private SearchSpec? discoverSearch;
    private uint? discoverOffset;
    private uint currentOffset;

    private async Task OpenDiscover()
    {
        SettingsPage.Visibility = Visibility.Collapsed;
        LibraryPage.Visibility = Visibility.Collapsed;
        DiscoverPage.Visibility = Visibility.Visible;
        Navigation.SelectedItem = DiscoverNavigation;
        Navigation.IsPaneOpen = false;
        DiscoverQuery.Focus(FocusState.Programmatic);
        var search = CurrentDiscoverSearch();
        if (discoverSearch != search)
            await SubmitDiscoverSearch();
    }

    private void CloseDiscover(object sender, RoutedEventArgs e)
    {
        ShowLibrary();
    }

    private Task DiscoverAction(Func<Task> action) => Run(async () =>
    {
        try { await action(); }
        catch (Exception error) { DiscoverStatus.Text = error.Message; }
    }, waitForCore: true);

    private async void DiscoverSearch(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs e) =>
        await SubmitDiscoverSearch();

    private async void DiscoverSearchClick(object sender, RoutedEventArgs e) =>
        await SubmitDiscoverSearch();

    private async void DiscoverFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ready && DiscoverPage.Visibility == Visibility.Visible)
            await SubmitDiscoverSearch();
    }

    private SearchSpec CurrentDiscoverSearch()
    {
        var kind = (DiscoverKind.SelectedItem as ComboBoxItem)?.Tag as string ?? "modpack";
        return new SearchSpec(DiscoverQuery.Text.Trim(), kind, kind == "mod" ? SelectedProfile?.id : null);
    }

    private Task SubmitDiscoverSearch()
    {
        var search = CurrentDiscoverSearch();
        return DiscoverAction(async () =>
        {
            if (search.Kind == "mod" && search.Profile is null)
            {
                ClearDiscoverResults();
                DiscoverStatus.Text = "Спочатку виберіть профіль у бібліотеці.";
                return;
            }
            await SearchDiscover(search, 0);
        });
    }

    private async void DiscoverPrevious(object sender, RoutedEventArgs e)
    {
        if (discoverSearch is { } search && currentOffset > 0)
        {
            var offset = currentOffset >= 20 ? currentOffset - 20 : 0;
            await DiscoverAction(() => SearchDiscover(search, offset));
        }
    }

    private async void DiscoverNext(object sender, RoutedEventArgs e)
    {
        if (discoverSearch is { } search && discoverOffset is uint offset)
            await DiscoverAction(() => SearchDiscover(search, offset));
    }

    private async Task SearchDiscover(SearchSpec search, uint offset)
    {
        DiscoverStatus.Text = "Шукаємо…";
        ClearDiscoverResults();
        var page = await core.Call<SearchPage>(new
        {
            method = "discover", action = "search", query = search.Query,
            kind = search.Kind, profile = search.Profile, offset = offset.ToString(System.Globalization.CultureInfo.InvariantCulture)
        });
        discoverSearch = search;
        currentOffset = offset;
        discoverOffset = page.next_offset;
        DiscoverResults.ItemsSource = page.hits;
        DiscoverBack.IsEnabled = offset > 0;
        DiscoverMore.IsEnabled = discoverOffset.HasValue;
        DiscoverStatus.Text = page.hits.Length == 0 ? "Нічого не знайдено." : "";
    }

    private void ClearDiscoverResults()
    {
        discoverSearch = null;
        discoverOffset = null;
        DiscoverResults.ItemsSource = null;
        DiscoverBack.IsEnabled = false;
        DiscoverMore.IsEnabled = false;
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
