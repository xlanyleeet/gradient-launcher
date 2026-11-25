using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
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
    public sealed record DiscoverVersion(string id, string name, string channel, string[]? game_versions = null)
    {
        public string Label => game_versions is { Length: > 0 }
            ? $"{name} · Minecraft {string.Join(", ", game_versions)} · {channel}"
            : $"{name} · {channel}";
    }

    private sealed record SearchPage(DiscoverHit[] hits, uint? next_offset);
    private sealed record ProjectDetails(string description, string body, string license,
        uint downloads, string updated, string url, string? gallery, string[]? gallery_images, string? icon, string? license_id = null, string[]? gallery_originals = null);
    private sealed record LinkedProject(string id, string name, string kind, string? preferred_version);
    private sealed record SearchSpec(string Query, string Kind, string? Profile, string? Sort, string? GameVersion, string? Loader, bool CurseForge);
    private SearchSpec? discoverSearch;
    private string discoverKind = "modpack";
    private uint? discoverOffset;
    private uint currentOffset;
    private double discoverScrollOffset;

    private static ScrollViewer? FindScrollViewer(DependencyObject element)
    {
        if (element is ScrollViewer viewer) return viewer;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(element, index)) is { } child) return child;
        return null;
    }

    private static void FadeIn(UIElement target)
    {
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) return;
        var fade = new Microsoft.UI.Xaml.Media.Animation.FadeInThemeAnimation();
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(fade, target);
        var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        storyboard.Children.Add(fade);
        storyboard.Begin();
    }

    private void ShowDiscoverPage()
    {
        var returningFromProject = ProjectPage.Visibility == Visibility.Visible;
        ProjectPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;
        LibraryPage.Visibility = Visibility.Collapsed;
        LibraryHome.Visibility = Visibility.Collapsed;
        BackButton.Visibility = Visibility.Visible;
        DiscoverPage.Visibility = Visibility.Visible;
        Navigation.SelectedItem = DiscoverNavigation;
        DiscoverQuery.Focus(FocusState.Programmatic);
        if (returningFromProject)
        {
            DiscoverResults.UpdateLayout();
            FindScrollViewer(DiscoverResults)?.ChangeView(null, discoverScrollOffset, null, true);
            FadeIn(DiscoverPage);
        }
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

    private bool IsCurseForge => DiscoverSource.SelectedIndex == 1;

    private async void DiscoverSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready) return;
        if (IsCurseForge && discoverKind is not ("mod" or "modpack")) discoverKind = "modpack";
        foreach (var button in DiscoverCategories.Children.OfType<ToggleButton>())
        {
            var kind = (string)button.Tag;
            button.IsChecked = kind == discoverKind;
            button.Visibility = !IsCurseForge || kind is "mod" or "modpack" ? Visibility.Visible : Visibility.Collapsed;
        }
        UpdateDiscoverFilters();
        await SubmitDiscoverSearch();
    }

    private void UpdateDiscoverFilters()
    {
        DiscoverPackFilters.Visibility = IsCurseForge || discoverKind == "modpack" ? Visibility.Visible : Visibility.Collapsed;
        DiscoverPopularity.Content = Tr(discoverKind == "server" ? "За активністю гравців" : "За завантаженнями");
        DiscoverOpenLink.Visibility = IsCurseForge ? Visibility.Collapsed : Visibility.Visible;
        DiscoverQuery.PlaceholderText = IsCurseForge ? Tr("Знайти на CurseForge") : Tr("Знайти на Modrinth");
    }

    private async void DiscoverCategoryClick(object sender, RoutedEventArgs e)
    {
        var selected = (ToggleButton)sender;
        foreach (var category in DiscoverCategories.Children.OfType<ToggleButton>())
            category.IsChecked = category == selected;
        var kind = (string)selected.Tag;
        if (discoverKind == kind) return;
        discoverKind = kind;
        UpdateDiscoverFilters();
        await SubmitDiscoverSearch();
    }

    private SearchSpec CurrentDiscoverSearch()
    {
        var kind = discoverKind;
        return new SearchSpec(DiscoverQuery.Text.Trim(), kind, kind is "modpack" or "server" ? null : SelectedProfile?.id,
            (DiscoverSort.SelectedItem as ComboBoxItem)?.Tag as string,
            IsCurseForge || kind == "modpack" ? DiscoverGameVersion.Text.Trim() : null,
            IsCurseForge || kind == "modpack" ? (DiscoverLoader.SelectedItem as ComboBoxItem)?.Tag as string : null, IsCurseForge);
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
        DiscoverResults.IsEnabled = false;
        DiscoverBack.IsEnabled = DiscoverMore.IsEnabled = false;
        SearchPage page;
        try
        {
            page = await core.Call<SearchPage>(new
            {
                method = "discover", action = "search", query = search.Query,
                source = search.CurseForge ? "curseforge" : "modrinth",
                kind = search.Kind, profile = search.Profile, offset = offset.ToString(System.Globalization.CultureInfo.InvariantCulture),
                sort = search.Sort, game_version = search.GameVersion, loader = search.Loader
            });
        }
        catch
        {
            ClearDiscoverResults();
            throw;
        }
        finally { DiscoverResults.IsEnabled = true; }
        if (search.Kind != discoverKind || search.CurseForge != IsCurseForge) return;
        discoverSearch = search;
        currentOffset = offset;
        discoverOffset = page.next_offset;
        DiscoverResults.ItemsSource = page.hits;
        FadeIn(DiscoverResults);
        var number = offset == 0 ? 1 : (offset - 1) / 20 + 2;
        DiscoverCurrentPage.Text = number.ToString();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(DiscoverCurrentPage, Tr("Поточна сторінка {0}", number));
        DiscoverBack.Content = (number - 1).ToString();
        DiscoverMore.Content = (number + 1).ToString();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(DiscoverBack, Tr("Сторінка {0}", number - 1));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(DiscoverMore, Tr("Сторінка {0}", number + 1));
        DiscoverBack.Visibility = offset > 0 ? Visibility.Visible : Visibility.Collapsed;
        DiscoverMore.Visibility = discoverOffset.HasValue ? Visibility.Visible : Visibility.Collapsed;
        DiscoverPagination.Visibility = offset > 0 || discoverOffset.HasValue || page.hits.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        DiscoverBack.IsEnabled = offset > 0;
        DiscoverMore.IsEnabled = discoverOffset.HasValue;
        DiscoverStatus.Text = page.hits.Length == 0 ? Tr("Нічого не знайдено.") : "";
    }

    private void ClearDiscoverResults()
    {
        discoverSearch = null;
        discoverOffset = null;
        DiscoverPagination.Visibility = Visibility.Collapsed;
        DiscoverResults.ItemsSource = null;
        DiscoverBack.IsEnabled = false;
        DiscoverMore.IsEnabled = false;
    }

    private async void SelectDiscoverResult(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not DiscoverHit hit || discoverSearch is not { } search || search.Kind != discoverKind) return;
        if (search.CurseForge)
        {
            if (Uri.TryCreate(hit.url, UriKind.Absolute, out var url) && url.Scheme == "https" && url.Host == "www.curseforge.com")
                await Windows.System.Launcher.LaunchUriAsync(url);
            return;
        }
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
        var stickyAdd = new Button { Content = Tr("Додати сервер"), IsEnabled = canAdd };
        ProjectStickyAction.Content = stickyAdd;
        Task AddServer() => DiscoverAction(async () =>
        {
            if (profile is null || !add.IsEnabled || ProjectPage.Visibility != Visibility.Visible) return;
            add.IsEnabled = stickyAdd.IsEnabled = false;
            try
            {
                await core.Call<JsonElement>(new { method = "save_server", id = profile.id, name = server.name, address = server.address });
                serverViewKey = null;
                if (projectNotice is { } notice) notice.Text = Tr("Сервер додано.");
            }
            catch { add.IsEnabled = stickyAdd.IsEnabled = canAdd; throw; }
        });
        add.Click += async (_, _) => await AddServer();
        stickyAdd.Click += async (_, _) => await AddServer();
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
    private bool projectHeaderShown;
    private Storyboard? projectHeaderAnimation;

    private void ProjectScrollChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        var threshold = ProjectContent.Children.FirstOrDefault() is FrameworkElement hero ? hero.ActualHeight : 350;
        var show = ProjectScroll.VerticalOffset >= threshold && threshold > 0;
        if (show == projectHeaderShown) return;
        projectHeaderShown = show;
        projectHeaderAnimation?.Stop();
        projectHeaderAnimation = null;
        ProjectStickyHeader.IsHitTestVisible = show;

        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            ProjectStickyHeader.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        if (show) ProjectStickyHeader.Visibility = Visibility.Visible;
        Timeline fade = show ? new FadeInThemeAnimation() : new FadeOutThemeAnimation();
        Storyboard.SetTarget(fade, ProjectStickyHeader);
        var storyboard = new Storyboard();
        storyboard.Children.Add(fade);
        projectHeaderAnimation = storyboard;
        storyboard.Completed += (_, _) =>
        {
            if (projectHeaderAnimation != storyboard) return;
            if (!projectHeaderShown) ProjectStickyHeader.Visibility = Visibility.Collapsed;
            storyboard.Stop();
            projectHeaderAnimation = null;
        };
        storyboard.Begin();
    }

    private StackPanel BeginProjectPage(string title, string? subtitle, string description, string? icon, string? banner, string? url)
    {
        discoverScrollOffset = FindScrollViewer(DiscoverResults)?.VerticalOffset ?? 0;
        ProjectContent.Children.Clear();
        DiscoverPage.Visibility = Visibility.Collapsed;
        ProjectPage.Visibility = Visibility.Visible;
        projectHeaderAnimation?.Stop();
        projectHeaderAnimation = null;
        projectHeaderShown = false;
        ProjectStickyHeader.Visibility = Visibility.Collapsed;
        ProjectScroll.ChangeView(null, 0, null, true);
        ProjectStickyTitle.Text = title;
        ProjectStickyLink.Content = ModrinthIconLink(url);
        ProjectStickySubtitle.Text = subtitle ?? "";
        ProjectStickyIcon.Source = ProjectImage(icon, 88)?.Source;
        ProjectStickyIcon.Visibility = ProjectStickyIcon.Source is null ? Visibility.Collapsed : Visibility.Visible;
        ProjectStickyAction.Content = null;
        FadeIn(ProjectPage);
        var hero = new Grid { MinHeight = 350, CornerRadius = new CornerRadius(16), Background = ProjectPage.Background };
        if (Uri.TryCreate(banner, UriKind.Absolute, out var artwork) && artwork.Scheme == Uri.UriSchemeHttps)
        {
            hero.Background = new ImageBrush
            {
                ImageSource = new BitmapImage(artwork),
                Stretch = Stretch.UniformToFill, AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Center,
            };
            var shade = (ProjectPage.Background as SolidColorBrush)?.Color
                ?? (ProjectPage.ActualTheme == ElementTheme.Dark ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White);
            var fade = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 0),
            };
            fade.GradientStops.Add(new GradientStop { Color = shade, Offset = 0 });
            fade.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(232, shade.R, shade.G, shade.B), Offset = .45 });
            fade.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0, shade.R, shade.G, shade.B), Offset = 1 });
            hero.Children.Add(new Border { Background = fade, IsHitTestVisible = false });
        }
        var header = new StackPanel { Spacing = 10, Padding = new Thickness(24), MaxWidth = 540,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom };
        if (ProjectImage(icon, 88) is { } logo)
        {
            logo.Width = logo.Height = 68;
            logo.HorizontalAlignment = HorizontalAlignment.Left;
            header.Children.Add(logo);
        }
        var titleRow = new Grid { ColumnSpacing = 6, HorizontalAlignment = HorizontalAlignment.Left };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition());
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.Children.Add(new TextBlock { Text = title, FontSize = 32, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        if (ModrinthIconLink(url) is { } projectLink)
        {
            Grid.SetColumn(projectLink, 1);
            titleRow.Children.Add(projectLink);
        }
        header.Children.Add(titleRow);
        if (!string.IsNullOrWhiteSpace(subtitle)) header.Children.Add(new TextBlock { Text = subtitle });
        header.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap });
        var notice = new TextBlock { TextWrapping = TextWrapping.Wrap };
        projectNotice = notice;
        ProjectStickyStatus.Text = "";
        ProjectStickyStatus.Visibility = Visibility.Collapsed;
        notice.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) =>
        {
            if (projectNotice != notice) return;
            ProjectStickyStatus.Text = notice.Text;
            ProjectStickyStatus.Visibility = string.IsNullOrEmpty(notice.Text) ? Visibility.Collapsed : Visibility.Visible;
        });
        header.Children.Add(projectNotice);
        hero.Children.Add(header);
        ProjectContent.Children.Add(hero);
        return header;
    }

    private static ScrollViewer ProjectMetadata(ProjectDetails details)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        void Add(Symbol symbol, string value, string label)
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            item.Children.Add(new SymbolIcon(symbol));
            item.Children.Add(new TextBlock { Text = value, VerticalAlignment = VerticalAlignment.Center });
            ToolTipService.SetToolTip(item, label);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, label);
            row.Children.Add(item);
        }

        var downloads = details.downloads switch
        {
            >= 999_950_000 => Tr("{0:0.#} млрд", details.downloads / 1_000_000_000m),
            >= 999_950 => Tr("{0:0.#} млн", details.downloads / 1_000_000m),
            >= 1_000 => Tr("{0:0.#} тис.", details.downloads / 1_000m),
            _ => Tr("{0:N0}", details.downloads)
        };
        Add(Symbol.Download, downloads, Tr("{0:N0} завантажень", details.downloads));
        Add(Symbol.Document, details.license_id ?? Tr("Ліцензія"), Tr("Ліцензія: {0}", details.license));
        Add(Symbol.Calendar, details.updated, Tr("Оновлено {0}", details.updated));
        return new ScrollViewer
        {
            Content = row, HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
    }

    private static HyperlinkButton? ModrinthIconLink(string? address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
            return null;

        var link = new HyperlinkButton
        {
            NavigateUri = url, Content = new SymbolIcon(Symbol.Link),
            Padding = new Thickness(6), VerticalAlignment = VerticalAlignment.Center
        };
        var label = Tr("Відкрити на Modrinth");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(link, label);
        ToolTipService.SetToolTip(link, label);
        return link;
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
        var header = BeginProjectPage(name, author, details.description, details.icon,
            details.gallery ?? details.gallery_images?.FirstOrDefault(), details.url);
        var notice = projectNotice!;
        header.Children.Add(ProjectMetadata(details));
        if (versions.Length == 0)
        {
            notice.Text = needsProfile ? Tr("Виберіть профіль для встановлення контенту.") : Tr("Сумісних версій немає.");
        }
        else
        {
            var picker = new ComboBox { ItemsSource = versions, DisplayMemberPath = "Label", HorizontalAlignment = HorizontalAlignment.Stretch };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(picker, Tr("Версія"));
            picker.SelectedItem = preferredVersion is null ? versions.FirstOrDefault() : versions.FirstOrDefault(item => item.id == preferredVersion);
            if (picker.SelectedItem is null) notice.Text = Tr("Версія за посиланням недоступна. Можна вибрати іншу.");
            var install = new Button { Content = Tr("Встановити"), IsEnabled = picker.SelectedItem is not null };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(install, Tr("Встановити"));
            var stickyInstall = new Button { Content = Tr("Встановити") };
            ProjectStickyAction.Content = stickyInstall;
            var progress = new ProgressRing
            {
                Width = 24, Height = 24, Visibility = Visibility.Collapsed,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(progress, Tr("Встановлюємо…"));
            var installArea = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
            installArea.Children.Add(install);
            installArea.Children.Add(progress);
            void ShowVersion()
            {
                stickyInstall.IsEnabled = install.IsEnabled = picker.SelectedItem is not null;
                ToolTipService.SetToolTip(picker, (picker.SelectedItem as DiscoverVersion)?.Label);
            }
            picker.SelectionChanged += (_, _) => ShowVersion();
            ShowVersion();
            Task InstallVersion() => DiscoverAction(async () =>
            {
                if (!install.IsEnabled || picker.SelectedItem is not DiscoverVersion version ||
                    ProjectPage.Visibility != Visibility.Visible || projectNotice != notice) return;
                picker.IsEnabled = install.IsEnabled = stickyInstall.IsEnabled = false;
                stickyInstall.Content = Tr("Встановлюємо…");
                notice.Text = Tr("Встановлюємо…");
                install.Opacity = 0;
                progress.Visibility = Visibility.Visible;
                progress.IsActive = true;
                FadeIn(progress);
                try
                {
                    await core.Call<JsonElement>(new { method = "discover", action = "install", project = id, version = version.id, kind, profile });
                    loadedTab = null;
                    notice.Text = kind == "modpack" ? Tr("Модпак додано до черги.") : Tr("Контент встановлено.");
                }
                finally
                {
                    progress.IsActive = false;
                    progress.Visibility = Visibility.Collapsed;
                    install.Opacity = 1;
                    FadeIn(install);
                    picker.IsEnabled = true;
                    stickyInstall.Content = Tr("Встановити");
                    ShowVersion();
                }
            });
            install.Click += async (_, _) => await InstallVersion();
            stickyInstall.Click += async (_, _) => await InstallVersion();
            var controls = new Grid { ColumnSpacing = 8, MaxWidth = 492 };
            controls.ColumnDefinitions.Add(new ColumnDefinition());
            controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(installArea, 1);
            controls.Children.Add(picker);
            controls.Children.Add(installArea);
            controls.Margin = new Thickness(0, 24, 0, 0);
            header.Children.Add(controls);
        }

        var gallery = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        var originals = details.gallery_originals ?? [];
        var galleryItems = (details.gallery_images ?? [])
            .Select((address, index) => (Thumbnail: address, Original: index < originals.Length ? originals[index] : address))
            .Where(item => Uri.TryCreate(item.Thumbnail, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            .ToArray();
        var screenshots = galleryItems.Select(item => item.Original).ToArray();
        for (var index = 0; index < screenshots.Length; index++)
        {
            var position = index;
            var image = ProjectImage(galleryItems[position].Thumbnail, 480)!;
            image.Width = 280;
            image.Height = 156;
            var thumbnail = new Button { Content = image, Padding = new Thickness(0) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(thumbnail, Tr("Відкрити скриншот {0}", position + 1));
            thumbnail.Click += async (_, _) => await ViewProjectScreenshots(screenshots, position);
            gallery.Children.Add(thumbnail);
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
            Uri.TryCreate(details.url, UriKind.Absolute, out var projectUrl);
            ProjectContent.Children.Add(ProjectMarkdown.Render(details.body, projectUrl));
        }
        DiscoverStatus.Text = "";
    }

    private async Task ViewProjectScreenshots(string[] screenshots, int position)
    {
        var root = Layout.XamlRoot;
        var image = new Image { Stretch = Stretch.Uniform };
        var previous = new Button { Content = new SymbolIcon(Symbol.Back) };
        var next = new Button { Content = new SymbolIcon(Symbol.Forward) };
        var count = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(previous, Tr("Попередній скриншот"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(next, Tr("Наступний скриншот"));
        var navigation = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 12 };
        navigation.Children.Add(previous);
        navigation.Children.Add(count);
        navigation.Children.Add(next);
        var content = new Grid { RowSpacing = 12 };
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(navigation, 1);
        content.Children.Add(image);
        content.Children.Add(navigation);
        var dialog = new ContentDialog
        {
            RequestedTheme = Layout.ActualTheme,
            XamlRoot = root, Title = Tr("Скриншоти"), Content = content,
            CloseButtonText = Tr("Закрити"), DefaultButton = ContentDialogButton.Close
        };
        dialog.Resources["ContentDialogMaxWidth"] = 1008d;
        void FitViewport()
        {
            content.Width = Math.Clamp(root.Size.Width - 96, 180, 960);
            content.Height = Math.Clamp(root.Size.Height - 256, 48, 560);
        }
        void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => FitViewport();
        void ShowScreenshot()
        {
            var bitmap = new BitmapImage(new Uri(screenshots[position]));
            image.Source = bitmap;
            count.Text = Tr("{0} із {1}", position + 1, screenshots.Length);
            previous.IsEnabled = position > 0;
            next.IsEnabled = position + 1 < screenshots.Length;
        }
        previous.Click += (_, _) => { position--; ShowScreenshot(); };
        next.Click += (_, _) => { position++; ShowScreenshot(); };
        ShowScreenshot();
        FitViewport();
        root.Changed += OnRootChanged;
        try { await dialog.ShowAsync(); }
        finally { root.Changed -= OnRootChanged; }
    }
}
