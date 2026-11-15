using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

using static Gradient.InterfaceText;

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
    private sealed record ProjectDetails(string description, string body, string license,
        uint downloads, string updated, string url, string? gallery);
    private sealed record LinkedProject(string id, string name, string kind, string? preferred_version);
    private sealed record SearchSpec(string Query, string Kind, string? Profile, string? Sort, string? GameVersion, string? Loader);
    private SearchSpec? discoverSearch;
    private uint? discoverOffset;
    private uint currentOffset;

    private void ShowDiscoverPage()
    {
        SettingsPage.Visibility = Visibility.Collapsed;
        LibraryPage.Visibility = Visibility.Collapsed;
        DiscoverPage.Visibility = Visibility.Visible;
        Navigation.SelectedItem = DiscoverNavigation;
        Navigation.IsPaneOpen = false;
        DiscoverQuery.Focus(FocusState.Programmatic);
    }

    private async Task OpenDiscover()
    {
        ShowDiscoverPage();
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
        {
            DiscoverPackFilters.Visibility = (DiscoverKind.SelectedItem as ComboBoxItem)?.Tag as string == "modpack"
                ? Visibility.Visible : Visibility.Collapsed;
            await SubmitDiscoverSearch();
        }
    }

    private SearchSpec CurrentDiscoverSearch()
    {
        var kind = (DiscoverKind.SelectedItem as ComboBoxItem)?.Tag as string ?? "modpack";
        return new SearchSpec(DiscoverQuery.Text.Trim(), kind, kind == "mod" ? SelectedProfile?.id : null,
            (DiscoverSort.SelectedItem as ComboBoxItem)?.Tag as string,
            kind == "modpack" ? DiscoverGameVersion.Text.Trim() : null,
            kind == "modpack" ? (DiscoverLoader.SelectedItem as ComboBoxItem)?.Tag as string : null);
    }

    private Task SubmitDiscoverSearch()
    {
        var search = CurrentDiscoverSearch();
        return DiscoverAction(async () =>
        {
            if (search.Kind == "mod" && search.Profile is null)
            {
                ClearDiscoverResults();
                DiscoverStatus.Text = Tr("Спочатку виберіть профіль у бібліотеці.");
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
        DiscoverStatus.Text = Tr("Шукаємо…");
        ClearDiscoverResults();
        var page = await core.Call<SearchPage>(new
        {
            method = "discover", action = "search", query = search.Query,
            kind = search.Kind, profile = search.Profile, offset = offset.ToString(System.Globalization.CultureInfo.InvariantCulture),
            sort = search.Sort, game_version = search.GameVersion, loader = search.Loader
        });
        discoverSearch = search;
        currentOffset = offset;
        discoverOffset = page.next_offset;
        DiscoverResults.ItemsSource = page.hits;
        DiscoverBack.IsEnabled = offset > 0;
        DiscoverMore.IsEnabled = discoverOffset.HasValue;
        DiscoverStatus.Text = page.hits.Length == 0 ? Tr("Нічого не знайдено.") : "";
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
        await DiscoverAction(() => ShowProject(hit.id, hit.name, search.Kind, search.Profile, null));
    }

    private async void OpenLinkClick(object sender, RoutedEventArgs e)
    {
        var input = new TextBox { PlaceholderText = "https://modrinth.com/modpack/…" };
        await DiscoverAction(async () =>
        {
            if (await Editor(Tr("Відкрити посилання Modrinth"), input, Tr("Відкрити")).ShowAsync() == ContentDialogResult.Primary)
                await ResolveAndShowLink(input.Text);
        });
    }

    internal Task OpenProjectLink(string url) => DiscoverAction(() => ResolveAndShowLink(url));

    private async Task ResolveAndShowLink(string url)
    {
        ShowDiscoverPage();
        DiscoverStatus.Text = Tr("Відкриваємо посилання…");
        var linked = await core.Call<LinkedProject>(new { method = "discover", action = "resolve_link", url });
        await ShowProject(linked.id, linked.name, linked.kind,
            linked.kind == "mod" ? SelectedProfile?.id : null, linked.preferred_version);
    }

    private async Task ShowProject(string id, string name, string kind, string? profile, string? preferredVersion)
    {
            DiscoverStatus.Text = Tr("Завантажуємо проєкт…");
            var details = await core.Call<ProjectDetails>(new
            {
                method = "discover", action = "project", project = id, kind
            });
            var needsProfile = kind == "mod" && profile is null;
            var versions = needsProfile ? Array.Empty<DiscoverVersion>() : await core.Call<DiscoverVersion[]>(new
            {
                method = "discover", action = "versions", project = id, kind, profile
            });
            var picker = new ComboBox { Header = Tr("Версія"), ItemsSource = versions, DisplayMemberPath = "Label" };
            picker.SelectedItem = preferredVersion is null ? versions.FirstOrDefault() : versions.FirstOrDefault(version => version.id == preferredVersion);
            var content = new StackPanel { Spacing = 10 };
            content.Children.Add(new TextBlock { Text = details.description, TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock
            {
                Text = Tr("{0:N0} завантажень · {1} · Оновлено {2}", details.downloads, details.license, details.updated),
                TextWrapping = TextWrapping.Wrap
            });
            if (Uri.TryCreate(details.url, UriKind.Absolute, out var projectUri) && projectUri.Scheme == Uri.UriSchemeHttps)
                content.Children.Add(new HyperlinkButton { Content = Tr("Відкрити на Modrinth"), NavigateUri = projectUri });
            if (Uri.TryCreate(details.gallery, UriKind.Absolute, out var imageUri) && imageUri.Scheme == Uri.UriSchemeHttps)
                content.Children.Add(new Image { Source = new BitmapImage(imageUri) { DecodePixelWidth = 700 }, MaxHeight = 180 });
            if (!string.IsNullOrWhiteSpace(details.body))
                content.Children.Add(new ScrollViewer
                {
                    MaxHeight = 180,
                    Content = new TextBlock { Text = details.body, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap }
                });
            content.Children.Add(versions.Length == 0
                ? new TextBlock { Text = needsProfile ? Tr("Виберіть профіль у бібліотеці, щоб установити мод.") : Tr("Сумісних версій немає.") }
                : picker);
            if (preferredVersion is not null && versions.Length > 0 && picker.SelectedItem is null)
                content.Children.Add(new TextBlock { Text = Tr("Версія за посиланням недоступна. Можна вибрати іншу.") });
            var dialog = Editor(name, new ScrollViewer { Content = content, MaxHeight = 430 }, Tr("Встановити"));
            dialog.IsPrimaryButtonEnabled = picker.SelectedItem is not null;
            picker.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = picker.SelectedItem is not null;
            DiscoverStatus.Text = "";
            if (await dialog.ShowAsync() != ContentDialogResult.Primary ||
                picker.SelectedItem is not DiscoverVersion version) return;
            DiscoverStatus.Text = Tr("Встановлюємо…");
            await core.Call<JsonElement>(new
            {
                method = "discover", action = "install", project = id, version = version.id, kind, profile
            });
            loadedTab = null;
            DiscoverStatus.Text = kind == "modpack" ? Tr("Модпак додано до черги.") : Tr("Мод встановлено.");
    }
}
