using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Gradient;

public sealed partial class MainWindow : Window
{
    public sealed record Profile(string id, string name, string version, string loader, string? last_played, bool running, bool quarantined, string? icon)
    {
        public BitmapImage? IconSource => Uri.TryCreate(icon, UriKind.Absolute, out var uri) && uri.IsFile
            ? new BitmapImage(uri) : null;
    }
    public sealed record Job(string job_id, string? instance_id, string status, string phase,
        bool paused, bool can_pause, bool can_cancel, double? progress, string? error);

    private readonly CoreClient core = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private Profile[] library = [];
    private Job[] jobs = [];
    private Profile? SelectedProfile => Profiles.SelectedItem as Profile;
    private string? coverId;
    private bool busy;
    private readonly SemaphoreSlim coreOperation = new(1);
    private bool closed;
    private bool ready;
    private bool filteringProfiles;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(WindowTitleBar);
        Navigation.SelectedItem = LibraryNavigation;
        ready = true;
        AppWindow.Resize(new SizeInt32(1160, 770));
        timer.Tick += async (_, _) => await Run(Update);
        Closed += (_, _) =>
        {
            closed = true;
            timer.Stop();
            if (!busy) core.Dispose();
        };
    }

    private async void Start(object sender, RoutedEventArgs e)
    {
        Layout.Loaded -= Start;
        await Run(RefreshAccountAvatar);
        await Run(Update);
        var importPath = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(arg =>
            Path.GetExtension(arg).Equals(".mrpack", StringComparison.OrdinalIgnoreCase) && File.Exists(arg));
        if (importPath is not null)
            await Run(async () =>
            {
                var dialog = Editor("Імпортувати модпак?", Path.GetFileName(importPath), "Імпортувати");
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                    await ApplyProfileChange(new { method = "import", path = Path.GetFullPath(importPath) },
                        "Модпак додано до черги.");
            });
        if (!closed) timer.Start();
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
        var id = SelectedProfile?.id;
        var filtered = library.Where(p => p.name.Contains(Search.Text, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        filteringProfiles = true;
        try
        {
            Profiles.ItemsSource = filtered;
            Profiles.SelectedItem = filtered.FirstOrDefault(p => p.id == id) ?? filtered.FirstOrDefault();
        }
        finally { filteringProfiles = false; }
    }

    private async void SearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs e)
    {
        if (!ready) return;
        FilterProfiles();
        await Run(LoadSelected);
    }

    private async Task LoadSelected()
    {
        var selected = SelectedProfile;
        Heading.Text = selected?.name ?? "Бібліотека";
        Metadata.Text = selected is null ? "Виберіть профіль або імпортуйте модпак."
            : $"Minecraft {selected.version}   ·   {selected.loader}   ·   Останній запуск  {selected.last_played ?? "—"}";
        PlayButton.IsEnabled = selected is { quarantined: false } || selected?.running == true;
        ProfileActions.IsEnabled = selected is not null;
        PlayButton.Content = selected?.running == true ? "■  Зупинити" : "▶  Грати";
        if (selected is null)
        {
            Cover.Source = new BitmapImage(new Uri("ms-appx:///Assets/Cover.png"));
            coverId = null;
            GameLog.Text = "Виберіть профіль.";
            LogPicker.IsEnabled = false;
            logProfileId = null;
            ClearScreenshots("Виберіть профіль.");
            FileList.ItemsSource = null;
            ParentDirectoryButton.IsEnabled = false;
            fileProfile = null;
            ServerRows.Children.Clear();
            AddServerButton.IsEnabled = false;
            serverViewKey = null;
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
        if (jobs.Length == 0) Jobs.Children.Add(new TextBlock { Text = "Черга порожня." });
        foreach (var job in jobs)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock { Text = $"{job.instance_id ?? "Модпак"} · {job.status} · {job.phase}", VerticalAlignment = VerticalAlignment.Center });
            if (job.can_pause) AddAction(row, job, job.paused ? "resume" : "pause", job.paused ? "Продовжити" : "Пауза");
            if (job.can_cancel) AddAction(row, job, "cancel", "Скасувати");
            if (job.status is "failed" or "interrupted") AddAction(row, job, "retry", "Повторити");
            var details = new StackPanel { Spacing = 4 };
            details.Children.Add(row);
            if (job.progress is double progress)
                details.Children.Add(new ProgressBar { Maximum = 1, Value = progress });
            if (job.error is { } error)
                details.Children.Add(new TextBlock { Text = error, TextWrapping = TextWrapping.Wrap });
            if (job.status is "failed" or "interrupted" or "canceled")
            {
                var report = new Button { Content = "Діагностика" };
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
        var dialog = Editor("Діагностика встановлення", new ScrollViewer { Content = output, MaxHeight = 420 }, "Копіювати");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
        data.SetText(report);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
        Status.Text = "Звіт скопійовано.";
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

    private async void Select(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || filteringProfiles) return;
        if (SelectedProfile is not null)
        {
            ShowLibrary();
            Navigation.IsPaneOpen = false;
        }
        await Run(LoadSelected);
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
            Status.Text = profile.running ? "Зупиняємо Minecraft…" : "Готуємо Minecraft…";
            await core.Call<JsonElement>(new { method = profile.running ? "stop" : "play", id = profile.id });
            Status.Text = "";
            await Update();
        });
    }

    private async void Import(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".mrpack");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        await core.Call<JsonElement>(new { method = "import", path = file.Path });
        Status.Text = "Модпак додано до черги.";
        await Update();
    });

    private async void Login(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var input = new TextBox { PlaceholderText = "Ім’я гравця" };
        var dialog = new ContentDialog
        {
            XamlRoot = Layout.XamlRoot, Title = "Офлайн-акаунт", Content = input,
            PrimaryButtonText = "Зберегти", CloseButtonText = "Скасувати"
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await core.Call<JsonElement>(new { method = "offline_login", name = input.Text });
        await RefreshAccountAvatar();
        Status.Text = "Офлайн-акаунт вибрано.";
    });

    private async void MicrosoftLogin(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var url = await core.Call<string>(new { method = "begin_login" });
        var web = new WebView2 { Width = 650, Height = 440 };
        var dialog = new ContentDialog
        {
            XamlRoot = Layout.XamlRoot, Title = "Увійти в Microsoft", Content = web,
            CloseButtonText = "Скасувати"
        };
        string? code = null;
        web.NavigationStarting += (_, args) =>
        {
            var uri = new Uri(args.Uri);
            if (uri.Scheme != "https" || uri.Host != "login.live.com" || uri.AbsolutePath != "/oauth20_desktop.srf")
                return;
            args.Cancel = true;
            var query = new Windows.Foundation.WwwFormUrlDecoder(uri.Query);
            code = query.FirstOrDefault(pair => pair.Name == "code")?.Value;
            dialog.Hide();
        };
        try
        {
            web.Source = new Uri(url);
            await dialog.ShowAsync();
        }
        finally
        {
            web.Close();
            if (code is null) await core.Call<JsonElement>(new { method = "cancel_login" });
        }
        if (code is null) return;
        await core.Call<JsonElement>(new { method = "finish_login", code });
        await RefreshAccountAvatar();
        Status.Text = "Вхід у Microsoft виконано.";
    });
}
