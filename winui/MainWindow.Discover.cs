using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;

using static Gradient.InterfaceText;

namespace Gradient;

public sealed partial class MainWindow
{
    public sealed record DiscoverHit(string id, string name, string description, string author, string? icon,
        string? address, string? url)
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
        uint downloads, string updated, string url, string? gallery, string[]? gallery_images, string? icon);
    private sealed record LinkedProject(string id, string name, string kind, string? preferred_version);
    private sealed record SearchSpec(string Query, string Kind, string? Profile, string? Sort, string? GameVersion, string? Loader);
    private SearchSpec? discoverSearch;
    private string discoverKind = "modpack";
    private uint? discoverOffset;
    private uint currentOffset;

    private void ShowDiscoverPage()
    {
        ProjectPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;
        LibraryPage.Visibility = Visibility.Collapsed;
        LibraryHome.Visibility = Visibility.Collapsed;
        WindowTitleBar.IsBackButtonVisible = true;
        DiscoverPage.Visibility = Visibility.Visible;
        Navigation.SelectedItem = DiscoverNavigation;
        DiscoverQuery.Focus(FocusState.Programmatic);
    }

    private async Task OpenDiscover()
    {
        ShowDiscoverPage();
        var search = CurrentDiscoverSearch();
        if (discoverSearch != search)
            await SubmitDiscoverSearch();
    }

    private Task DiscoverAction(Func<Task> action) => Run(async () =>
    {
        try { await action(); }
        catch (Exception error)
        {
            if (ProjectPage.Visibility == Visibility.Visible && projectNotice is not null) projectNotice.Text = error.Message;
            else DiscoverStatus.Text = error.Message;
        }
    }, waitForCore: true);

    private async void DiscoverSearch(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs e) =>
        await SubmitDiscoverSearch();

    private async void DiscoverSearchClick(object sender, RoutedEventArgs e) =>
        await SubmitDiscoverSearch();

    private async void DiscoverCategoryClick(object sender, RoutedEventArgs e)
    {
        var selected = (ToggleButton)sender;
        foreach (var category in DiscoverCategories.Children.OfType<ToggleButton>())
            category.IsChecked = category == selected;
        var kind = (string)selected.Tag;
        if (discoverKind == kind) return;
        discoverKind = kind;
        DiscoverPackFilters.Visibility = kind == "modpack" ? Visibility.Visible : Visibility.Collapsed;
        DiscoverPopularity.Content = Tr(kind == "server" ? "За активністю гравців" : "За завантаженнями");
        await SubmitDiscoverSearch();
    }

    private SearchSpec CurrentDiscoverSearch()
    {
        var kind = discoverKind;
        return new SearchSpec(DiscoverQuery.Text.Trim(), kind, kind is "modpack" or "server" ? null : SelectedProfile?.id,
            (DiscoverSort.SelectedItem as ComboBoxItem)?.Tag as string,
            kind == "modpack" ? DiscoverGameVersion.Text.Trim() : null,
            kind == "modpack" ? (DiscoverLoader.SelectedItem as ComboBoxItem)?.Tag as string : null);
    }

    private Task SubmitDiscoverSearch()
    {
        var search = CurrentDiscoverSearch();
        return DiscoverAction(() => SearchDiscover(search, 0));
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
        if (search.Kind != discoverKind) return;
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
        if (e.ClickedItem is not DiscoverHit hit || discoverSearch is not { } search || search.Kind != discoverKind) return;
        await DiscoverAction(() => search.Kind == "server" ? ShowCatalogServer(hit)
            : ShowProject(hit.id, hit.name, search.Kind, search.Profile, null, hit.author));
    }

    private Task ShowCatalogServer(DiscoverHit server)
    {
        var profile = SelectedProfile;
        bool canAdd = profile is not null && !string.IsNullOrWhiteSpace(server.address);
        var content = BeginProjectPage(server.name, server.author, server.description, server.icon, null, server.url);
        if (server.address is { } address)
            content.Children.Add(new TextBlock { Text = address, IsTextSelectionEnabled = true });
        var add = new Button { Content = Tr("Додати сервер"), IsEnabled = canAdd };
        add.Click += async (_, _) => await DiscoverAction(async () =>
        {
            if (profile is null || !add.IsEnabled || ProjectPage.Visibility != Visibility.Visible) return;
            add.IsEnabled = false;
            try
            {
                await core.Call<JsonElement>(new { method = "save_server", id = profile.id, name = server.name, address = server.address });
                serverViewKey = null;
                if (projectNotice is { } notice) notice.Text = Tr("Сервер додано.");
            }
            catch { add.IsEnabled = canAdd; throw; }
        });
        content.Children.Add(add);
        return Task.CompletedTask;
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
            linked.kind == "modpack" ? null : SelectedProfile?.id, linked.preferred_version);
    }

    private TextBlock? projectNotice;

    private StackPanel BeginProjectPage(string title, string? subtitle, string description, string? icon, string? banner, string? url)
    {
        ProjectContent.Children.Clear();
        DiscoverPage.Visibility = Visibility.Collapsed;
        ProjectPage.Visibility = Visibility.Visible;
        ProjectPage.ChangeView(null, 0, null, true);
        var hero = new Grid { MinHeight = 250, CornerRadius = new CornerRadius(16) };
        if (ProjectImage(banner, 1100) is { } cover)
        {
            cover.Height = 336;
            hero.Children.Add(cover);
        }
        var header = new StackPanel { Spacing = 10, Padding = new Thickness(24), MaxWidth = 560,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
            Background = ProjectPage.Background };
        if (ProjectImage(icon, 88) is { } logo)
        {
            logo.Width = logo.Height = 68;
            logo.HorizontalAlignment = HorizontalAlignment.Left;
            header.Children.Add(logo);
        }
        header.Children.Add(new TextBlock { Text = title, FontSize = 32, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(subtitle)) header.Children.Add(new TextBlock { Text = subtitle });
        header.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap });
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            header.Children.Add(new HyperlinkButton { Content = Tr("Відкрити на Modrinth"), NavigateUri = uri });
        projectNotice = new TextBlock { TextWrapping = TextWrapping.Wrap };
        header.Children.Add(projectNotice);
        hero.Children.Add(header);
        ProjectContent.Children.Add(hero);
        return header;
    }

    private static Image? ProjectImage(string? url, int decodeWidth) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? new Image { Source = new BitmapImage(uri) { DecodePixelWidth = decodeWidth },
                Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill } : null;

    private async Task ShowProject(string id, string name, string kind, string? profile,
        string? preferredVersion, string? author = null)
    {
        DiscoverStatus.Text = Tr("Завантажуємо проєкт…");
        var details = await core.Call<ProjectDetails>(new { method = "discover", action = "project", project = id, kind });
        var needsProfile = kind != "modpack" && profile is null;
        var versions = needsProfile ? Array.Empty<DiscoverVersion>() : await core.Call<DiscoverVersion[]>(new
        {
            method = "discover", action = "versions", project = id, kind, profile
        });
        if (DiscoverPage.Visibility != Visibility.Visible) return;
        var header = BeginProjectPage(name, author, details.description, details.icon, details.gallery, details.url);
        var notice = projectNotice!;
        var metadata = Tr("{0:N0} завантажень · {1} · Оновлено {2}", details.downloads, details.license, details.updated);
        header.Children.Add(new TextBlock { Text = metadata, TextWrapping = TextWrapping.Wrap });
        if (versions.Length == 0)
        {
            notice.Text = needsProfile ? Tr("Виберіть профіль для встановлення контенту.") : Tr("Сумісних версій немає.");
        }
        else
        {
            var picker = new ComboBox { Header = Tr("Версія"), ItemsSource = versions, DisplayMemberPath = "Label", MaxWidth = 440 };
            picker.SelectedItem = preferredVersion is null ? versions.FirstOrDefault() : versions.FirstOrDefault(item => item.id == preferredVersion);
            if (picker.SelectedItem is null) notice.Text = Tr("Версія за посиланням недоступна. Можна вибрати іншу.");
            var install = new Button { Content = Tr("Встановити"), IsEnabled = picker.SelectedItem is not null };
            picker.SelectionChanged += (_, _) => install.IsEnabled = picker.SelectedItem is not null;
            install.Click += async (_, _) => await DiscoverAction(async () =>
            {
                if (!install.IsEnabled || picker.SelectedItem is not DiscoverVersion version ||
                    ProjectPage.Visibility != Visibility.Visible || projectNotice != notice) return;
                picker.IsEnabled = install.IsEnabled = false;
                notice.Text = Tr("Встановлюємо…");
                try
                {
                    await core.Call<JsonElement>(new { method = "discover", action = "install", project = id, version = version.id, kind, profile });
                    loadedTab = null;
                    notice.Text = kind == "modpack" ? Tr("Модпак додано до черги.") : Tr("Контент встановлено.");
                }
                finally { picker.IsEnabled = install.IsEnabled = true; }
            });
            header.Children.Add(picker);
            header.Children.Add(install);
        }

        var gallery = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        foreach (var address in details.gallery_images ?? [])
        {
            if (ProjectImage(address, 480) is not { } image) continue;
            image.Width = 280;
            image.Height = 156;
            gallery.Children.Add(image);
        }
        if (gallery.Children.Count > 0)
        {
            ProjectContent.Children.Add(new TextBlock { Text = Tr("Скриншоти"), FontSize = 20 });
            ProjectContent.Children.Add(new ScrollViewer { Content = gallery, HorizontalScrollMode = ScrollMode.Enabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollMode = ScrollMode.Disabled });
        }
        if (!string.IsNullOrWhiteSpace(details.body))
        {
            ProjectContent.Children.Add(new TextBlock { Text = Tr("Опис"), FontSize = 20 });
            ProjectContent.Children.Add(new TextBlock { Text = details.body, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        }
        DiscoverStatus.Text = "";
    }
}
