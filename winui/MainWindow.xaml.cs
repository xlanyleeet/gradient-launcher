using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinRT.Interop;

using static Gradient.InterfaceText;

namespace Gradient;

public sealed partial class MainWindow : Window
{
    public sealed record Profile(string id, string name, string version, string loader, string? last_played, bool running, bool quarantined, string? icon, ulong play_seconds = 0, bool is_modpack = false)
    {
        public bool ShowPlayTime { get; init; }
        public string GameLabel => ShowPlayTime
            ? $"{loader} {version} · {Tr("Час гри: {0} хв", play_seconds / 60)}"
            : $"{loader} {version}";
        public BitmapImage? IconSource => Uri.TryCreate(icon, UriKind.Absolute, out var uri) && uri.IsFile
            ? new BitmapImage(uri) : null;
    }
    public sealed record Job(string job_id, string? instance_id, string status, string phase,
        bool paused, bool can_pause, bool can_cancel, double? progress, string? error);

    private readonly CoreClient core = new();
    private CancellationTokenSource? libraryDebounceCts;
    private CancellationTokenSource? logDebounceCts;
    private Profile[] library = [];
    private Job[] jobs = [];
    private string? selectedProfileId;
    private Profile? SelectedProfile => library.FirstOrDefault(profile => profile.id == selectedProfileId);
    private string? coverId;
    private bool busy;
    private readonly SemaphoreSlim coreOperation = new(1);
    private bool closed;
    private bool ready;
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<NavigationViewItem> profileNavigationItems = [];
    private (string Id, string Name, string? Icon)[] sidebarEntries = [];

    public MainWindow()
    {
        InitializeComponent();
        BehaviorSection.Expanding += async (_, _) => await LoadBehaviorSettings();
        StorageSection.Expanding += async (_, _) => await LoadStorageSettings();
        Status.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) =>
        {
            LibraryStatus.Text = Status.Text;
            RefreshCopyDetails();
        });
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(WindowTitleBar);
        InitializeAppearance();
        AppWindow.TitleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Tall;
        WindowTitleBar.LayoutUpdated += (_, _) => UpdateTitleBar();
        BackButton.PointerEntered += (_, _) => AnimatedIcon.SetState(BackArrow, "PointerOver");
        BackButton.PointerExited += (_, _) => AnimatedIcon.SetState(BackArrow, "Normal");
        BackButton.AddHandler(UIElement.PointerPressedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => AnimatedIcon.SetState(BackArrow, "Pressed")), true);
        BackButton.AddHandler(UIElement.PointerReleasedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => AnimatedIcon.SetState(BackArrow, "Normal")), true);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Launcher.ico"));
        Navigation.SelectedItem = LibraryNavigation;
        ready = true;
        AppWindow.Resize(new SizeInt32(1160, 770));
        core.EventReceived += OnCoreEvent;
        Closed += (_, _) =>
        {
            closed = true;
            core.EventReceived -= OnCoreEvent;
            libraryDebounceCts?.Cancel();
            libraryDebounceCts?.Dispose();
            logDebounceCts?.Cancel();
            logDebounceCts?.Dispose();
            if (!busy) core.Dispose();
        };
    }

    private async void Start(object sender, RoutedEventArgs e)
    {
        Layout.Loaded -= Start;
        UpdateTitleBar();
        await Run(async () =>
        {
            try { ApplyPreferences(await PreferenceCall<NativePreferences>(new { action = "read" })); }
            catch (IOException) { }
        });
        await Run(RefreshAccountAvatar);
        await Run(Update);
        await Run(LoadQuickPlay);
        StartupProgress.Visibility = Visibility.Collapsed;
        started.TrySetResult();
    }

    private void OnCoreEvent(JsonElement ev)
    {
        if (closed) return;
        var clone = ev.Clone();
        DispatcherQueue.TryEnqueue(() => HandleCoreEvent(clone));
    }

    private void HandleCoreEvent(JsonElement ev)
    {
        if (closed || !ev.TryGetProperty("event", out var eventProp)) return;
        switch (eventProp.GetString())
        {
            case "job":
                if (ev.TryGetProperty("job", out var jobElement) && jobElement.Deserialize<Job>() is { } incoming)
                {
                    var list = jobs.ToList();
                    var index = list.FindIndex(existing => existing.job_id == incoming.job_id);
                    if (index >= 0) list[index] = incoming;
                    else list.Insert(0, incoming);
                    jobs = list.ToArray();
                    DrawJobs();
                    if (incoming.status is "succeeded" or "failed" or "canceled" or "interrupted")
                        ScheduleLibraryRefresh();
                }
                break;
            case "process":
            case "instance":
                ScheduleLibraryRefresh();
                break;
            case "log":
                if (ev.TryGetProperty("instance_id", out var idElement) &&
                    idElement.GetString() == SelectedProfile?.id)
                    ScheduleLiveLogRefresh();
                break;
        }
    }

    private void ScheduleLibraryRefresh()
    {
        libraryDebounceCts?.Cancel();
        libraryDebounceCts?.Dispose();
        var cts = libraryDebounceCts = new CancellationTokenSource();
        _ = DebounceAsync(100, cts.Token, () => Run(Update, waitForCore: true));
    }

    private void ScheduleLiveLogRefresh()
    {
        logDebounceCts?.Cancel();
        logDebounceCts?.Dispose();
        var cts = logDebounceCts = new CancellationTokenSource();
        _ = DebounceAsync(150, cts.Token, async () =>
        {
            if (LibraryPage.Visibility == Visibility.Visible &&
                Equals(Details.SelectedItem, LogTab) &&
                SelectedProfile is { } profile &&
                (LogPicker.SelectedItem as ArchivedLog ?? liveLog) == liveLog)
                await Run(() => LoadLog(profile), waitForCore: true);
        });
    }

    private async Task DebounceAsync(int delayMs, CancellationToken token, Func<Task> action)
    {
        try
        {
            await Task.Delay(delayMs, token);
            if (!closed && !token.IsCancellationRequested)
                await action();
        }
        catch (OperationCanceledException) { }
    }

    internal static bool IsModpackPath(string path) =>
        Path.GetExtension(path).Equals(".mrpack", StringComparison.OrdinalIgnoreCase) && File.Exists(path);

    internal async Task ReceiveModpack(string path)
    {
        if (!IsModpackPath(path)) return;
        await started.Task;
        await Run(async () =>
        {
            var dialog = Editor(Tr("Імпортувати модпак?"), Path.GetFileName(path), Tr("Імпортувати"));
            if (extraPreferences?.warn_on_unknown_modpacks == false || await dialog.ShowAsync() == ContentDialogResult.Primary)
                await ApplyProfileChange(new { method = "import", path = Path.GetFullPath(path) },
                    Tr("Модпак додано до черги."));
        }, waitForCore: true);
    }

    internal async Task ReceiveProjectLink(string url)
    {
        await started.Task;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            uri.Scheme == "gradient-native" && uri.Host == "launch")
            await OpenProfileShortcut(url);
        else
            await OpenProjectLink(url);
    }

    private async Task Run(Func<Task> action, bool waitForCore = false)
    {
        if (closed) return;
        if (waitForCore)
            await coreOperation.WaitAsync();
        else if (!coreOperation.Wait(0))
            return;
        if (closed)
        {
            coreOperation.Release();
            return;
        }
        busy = true;
        try { await action(); }
        catch (Exception error) { Status.Text = error.Message; }
        finally
        {
            busy = false;
            if (closed) core.Dispose();
            coreOperation.Release();
        }
    }

    private async Task Update()
    {
        var next = await core.Call<Profile[]>(new { method = "library" });
        if (!library.SequenceEqual(next))
        {
            ObserveGameWindows(next);
            library = next;
            FilterProfiles();
        }
        await LoadSelected();
        var queue = await core.Call<Job[]>(new { method = "queue" });
        if (jobs.SequenceEqual(queue) && Jobs.Children.Count > 0) return;
        jobs = queue;
        DrawJobs();
    }

    private void FilterProfiles()
    {
        var ordered = library.OrderBy(p => p.name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var filtered = ordered.Where(p => p.name.Contains(Search.Text, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        var cards = filtered.Select(profile => profile with { ShowPlayTime = extraPreferences?.show_play_time == true }).ToArray();
        LibraryCards.ItemsSource = cards;
        CompactLibrary.ItemsSource = cards;
        var sidebar = showAllProfiles ? ordered : ordered.Take(SidebarLimit).ToArray();
        MoreProfilesNavigation.Visibility = library.Length > SidebarLimit ? Visibility.Visible : Visibility.Collapsed;
        var moreLabel = Tr(showAllProfiles ? "Менше" : "Більше");
        MoreProfilesNavigation.Content = moreLabel;
        ToolTipService.SetToolTip(MoreProfilesNavigation, moreLabel);
        var entries = sidebar.Select(profile => (profile.id, profile.name, profile.icon)).ToArray();
        if (!sidebarEntries.SequenceEqual(entries))
        {
            foreach (var item in profileNavigationItems) Navigation.MenuItems.Remove(item);
            profileNavigationItems.Clear();
            var index = Navigation.MenuItems.IndexOf(MoreProfilesNavigation);
            foreach (var profile in sidebar)
            {
                var item = new NavigationViewItem
                {
                    Content = profile.name,
                    Tag = profile.id,
                    Icon = profile.IconSource is { } image
                        ? new ImageIcon { Source = image, Width = 26, Height = 26 }
                        : new SymbolIcon(Symbol.Library),
                };
                ToolTipService.SetToolTip(item, profile.name);
                Navigation.MenuItems.Insert(index++, item);
                profileNavigationItems.Add(item);
            }
            sidebarEntries = entries;
        }
        if (LibraryPage.Visibility == Visibility.Visible)
            Navigation.SelectedItem = profileNavigationItems.FirstOrDefault(item => Equals(item.Tag, selectedProfileId))
                ?? LibraryNavigation;
    }

    private void SearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs e)
    {
        if (!ready) return;
        if (HomeSearch.Text != sender.Text) HomeSearch.Text = sender.Text;
        FilterProfiles();
    }

    private async Task LoadSelected()
    {
        var selected = SelectedProfile;
        Heading.Text = selected?.name ?? Tr("Бібліотека");
        Metadata.Text = selected is null ? Tr("Виберіть профіль або імпортуйте модпак.")
            : Tr("Minecraft {0}   ·   {1}   ·   Останній запуск  {2}", selected.version, selected.loader, selected.last_played ?? "—");
        PlayButton.IsEnabled = selected is { quarantined: false } || selected?.running == true;
        ProfileActions.IsEnabled = selected is not null;
        UpdateModpackItem.Visibility = selected is { is_modpack: true } ? Visibility.Visible : Visibility.Collapsed;
        UpdateModpackItem.IsEnabled = selected is { is_modpack: true, running: false, quarantined: false };
        PlayButton.Content = selected?.running == true ? Tr("■  Зупинити") : Tr("▶  Грати");
        if (selected is null)
        {
            Cover.Source = new BitmapImage(new Uri("ms-appx:///Assets/Cover.png"));
            coverId = null;
            GameLog.Text = Tr("Виберіть профіль.");
            LogPicker.IsEnabled = false;
            logProfileId = null;
            ClearScreenshots(Tr("Виберіть профіль."));
            FileList.ItemsSource = null;
            ParentDirectoryButton.IsEnabled = false;
            fileProfile = null;
            ServerRows.Children.Clear();
            AddServerButton.IsEnabled = false;
            serverViewKey = null;
            loadedContent = [];
            UpdateAllContentButton.Visibility = Visibility.Collapsed;
            ContentRows.Children.Clear();
            WorldRows.Children.Clear();
            AddModButton.IsEnabled = false;
            return;
        }

        var nextCoverId = $"{selected.id}/{selected.running}/{selected.last_played}";
        if (coverId != nextCoverId)
        {
            var path = await core.Call<string?>(new { method = "cover", id = selected.id });
            if (SelectedProfile?.id != selected.id) return;
            Cover.Source = new BitmapImage(path is not null && File.Exists(path)
                ? new Uri(path) : new Uri("ms-appx:///Assets/Cover.png"));
            coverId = nextCoverId;
        }

        if (Equals(Details.SelectedItem, ContentTab) || Equals(Details.SelectedItem, WorldsTab))
        {
            await LoadProfileTab(selected);
            return;
        }
        if (Equals(Details.SelectedItem, ScreenshotsTab))
        {
            await LoadScreenshots(selected);
            return;
        }
        if (Equals(Details.SelectedItem, FilesTab))
        {
            if (fileProfile != selected.id) await LoadDirectory(selected, "");
            return;
        }
        if (Equals(Details.SelectedItem, ServersTab))
        {
            await LoadServers(selected);
            return;
        }
        if (!Equals(Details.SelectedItem, LogTab)) return;
        await LoadLog(selected);
    }

    private void DrawJobs()
    {
        Jobs.Children.Clear();
        if (jobs.Length == 0) Jobs.Children.Add(new TextBlock { Text = Tr("Черга порожня.") });
        foreach (var job in jobs)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock
            {
                Text = string.Join(" · ", job.instance_id ?? Tr("Модпак"), InstallLabel(job.status), InstallLabel(job.phase)),
                VerticalAlignment = VerticalAlignment.Center,
            });
            if (job.can_pause) AddAction(row, job, job.paused ? "resume" : "pause", job.paused ? Tr("Продовжити") : Tr("Пауза"));
            if (job.can_cancel) AddAction(row, job, "cancel", Tr("Скасувати"));
            if (job.status is "failed" or "interrupted") AddAction(row, job, "retry", Tr("Повторити"));
            var details = new StackPanel { Spacing = 4 };
            details.Children.Add(row);
            if (job.progress is double progress)
                details.Children.Add(new ProgressBar { Maximum = 1, Value = progress });
            if (job.error is { } error)
                details.Children.Add(new TextBlock { Text = error, TextWrapping = TextWrapping.Wrap });
            if (job.status is "failed" or "interrupted" or "canceled")
            {
                var report = new Button { Content = Tr("Діагностика") };
                report.Click += async (_, _) => await ShowJobDiagnostics(job);
                details.Children.Add(report);
            }
            Jobs.Children.Add(details);
        }
    }

    private Task ShowJobDiagnostics(Job job) => Run(async () =>
    {
        var report = await core.Call<string>(new { method = "job_diagnostics", id = job.job_id });
        var output = new TextBlock { Text = report, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap };
        var dialog = Editor(Tr("Діагностика встановлення"), new ScrollViewer { Content = output, MaxHeight = 420 }, Tr("Копіювати"));
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
        data.SetText(report);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
        Status.Text = Tr("Звіт скопійовано.");
    }, waitForCore: true);

    private void AddAction(StackPanel row, Job job, string action, string label)
    {
        var button = new Button { Content = label };
        button.Click += async (_, _) => await Run(async () =>
        {
            await core.Call<JsonElement>(new { method = "job", id = job.job_id, action });
            await Update();
        });
        row.Children.Add(button);
    }

    private async void Refresh(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        coverId = null;
        loadedTab = null;
        logProfileId = null;
        screenshotProfile = null;
        serverViewKey = null;
        if (SelectedProfile is { } selected && Equals(Details.SelectedItem, FilesTab))
            await LoadDirectory(selected, fileProfile == selected.id ? directoryPath : "");
        await Update();
    });

    private async void Play(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is not { } profile) return;
        await Run(async () =>
        {
            Status.Text = profile.running ? Tr("Зупиняємо Minecraft…") : Tr("Готуємо Minecraft…");
            await core.Call<JsonElement>(new { method = profile.running ? "stop" : "play", id = profile.id });
            Status.Text = "";
            await Update();
        });
    }

    private async void Import(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        ShowLibrary();
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".mrpack");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        if (extraPreferences?.warn_on_unknown_modpacks != false &&
            await Editor(Tr("Імпортувати модпак?"), file.Name, Tr("Імпортувати")).ShowAsync() != ContentDialogResult.Primary) return;
        await core.Call<JsonElement>(new { method = "import", path = file.Path });
        Status.Text = Tr("Модпак додано до черги.");
        await Update();
    });

    private async void Login(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var input = new TextBox { PlaceholderText = Tr("Ім’я гравця") };
        var dialog = new ContentDialog
        {
            RequestedTheme = Layout.ActualTheme,
            XamlRoot = Layout.XamlRoot, Title = Tr("Офлайн-акаунт"), Content = input,
            PrimaryButtonText = Tr("Зберегти"), CloseButtonText = Tr("Скасувати")
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await core.Call<JsonElement>(new { method = "offline_login", name = input.Text });
        await RefreshAccountAvatar();
        Status.Text = Tr("Офлайн-акаунт вибрано.");
    });

    private async void MicrosoftLogin(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var url = await core.Call<string>(new { method = "begin_login" });
        var tempDataDir = Path.Combine(Path.GetTempPath(), $"GradientOAuth-{Guid.NewGuid():N}");
        var rootSize = Layout.XamlRoot.Size;
        var web = new WebView2
        {
            Width = Math.Clamp(rootSize.Width - 120, 420, 680),
            Height = Math.Clamp(rootSize.Height - 220, 420, 560),
        };
        var dialog = new ContentDialog
        {
            RequestedTheme = Layout.ActualTheme,
            XamlRoot = Layout.XamlRoot, Title = Tr("Увійти в Microsoft"), Content = web,
            CloseButtonText = Tr("Скасувати")
        };
        dialog.Resources["ContentDialogMaxWidth"] = 760d;
        dialog.Resources["ContentDialogMaxHeight"] = 720d;
        string? code = null;
        string? oauthError = null;
        web.NavigationStarting += (_, args) =>
        {
            if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri))
                return;
            if (uri.Scheme != "https" || uri.Host != "login.live.com" || uri.AbsolutePath != "/oauth20_desktop.srf")
                return;
            args.Cancel = true;
            if (!string.IsNullOrEmpty(uri.Query))
            {
                var query = new Windows.Foundation.WwwFormUrlDecoder(uri.Query);
                code = query.FirstOrDefault(pair => pair.Name == "code")?.Value;
                if (code is null)
                {
                    oauthError = query.FirstOrDefault(pair => pair.Name == "error_description")?.Value
                        ?? query.FirstOrDefault(pair => pair.Name == "error")?.Value;
                }
            }
            dialog.Hide();
        };
        dialog.Opened += async (_, _) =>
        {
            try
            {
                var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateWithOptionsAsync(
                    null, tempDataDir, null);
                await web.EnsureCoreWebView2Async(env);
                web.Source = new Uri(url);
            }
            catch (Exception error)
            {
                oauthError = error.Message;
                dialog.Hide();
            }
        };
        try
        {
            await dialog.ShowAsync();
        }
        finally
        {
            web.Close();
            try
            {
                if (Directory.Exists(tempDataDir))
                    Directory.Delete(tempDataDir, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (code is null) await core.Call<JsonElement>(new { method = "cancel_login" });
        }
        if (code is null)
        {
            if (!string.IsNullOrWhiteSpace(oauthError))
                Status.Text = oauthError;
            return;
        }
        await core.Call<JsonElement>(new { method = "finish_login", code });
        await RefreshAccountAvatar();
        Status.Text = Tr("Вхід у Microsoft виконано.");
    });
}
