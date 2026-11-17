using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;
using static Gradient.InterfaceText;

namespace Gradient;

public sealed partial class MainWindow
{
    private sealed record LaunchHooks(string? pre_launch, string? wrapper, string? post_exit);
    private sealed record NativePreferences(
        bool compact_instance_cards,
        bool show_play_time,
        bool show_jump_in,
        bool hide_installed_modpacks,
        bool show_worlds_tab_in_instances,
        bool show_files_tab_in_instances,
        bool show_screenshots_tab_in_instances,
        bool show_skin_selector_in_sidebar,
        bool hide_on_process_start,
        bool refocus_on_game_close,
        bool warn_on_unknown_modpacks,
        bool always_show_copy_details,
        int max_concurrent_downloads, int max_concurrent_writes, string[][] custom_env_vars, LaunchHooks hooks);
    private sealed record DataLocations(string data, string backups);
    private sealed record StorageUsage(ulong unique_bytes, ulong shared_bytes, ulong unused_cache_bytes, ulong cache_limit_bytes);
    private sealed record StorageRepair(JsonElement[] issues);

    private NativePreferences? extraPreferences;

    private Task<T> PreferenceCall<T>(object request) =>
        core.Call<T>(new { method = "preferences", request });

    private void ApplyPreferences(NativePreferences preferences)
    {
        extraPreferences = preferences;
        RefreshCopyDetails();
        CompactLibrary.Visibility = preferences.compact_instance_cards ? Visibility.Visible : Visibility.Collapsed;
        LibraryCards.Visibility = preferences.compact_instance_cards ? Visibility.Collapsed : Visibility.Visible;
        int position = Details.Items.IndexOf(DownloadsTab) + 1;
        foreach (var (tab, visible) in new[]
        {
            (WorldsTab, preferences.show_worlds_tab_in_instances),
            (ScreenshotsTab, preferences.show_screenshots_tab_in_instances),
            (FilesTab, preferences.show_files_tab_in_instances),
        })
        {
            if (visible)
            {
                if (!Details.Items.Contains(tab)) Details.Items.Insert(position, tab);
                position++;
            }
            else if (Details.Items.Contains(tab))
            {
                if (Equals(Details.SelectedItem, tab)) Details.SelectedItem = ContentTab;
                Details.Items.Remove(tab);
            }
        }
        SkinNavigation.Visibility = preferences.show_skin_selector_in_sidebar ? Visibility.Visible : Visibility.Collapsed;
        if (!preferences.show_jump_in) QuickSection.Visibility = Visibility.Collapsed;
        FilterProfiles();
    }

    private async void EditExtraPreferences(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var values = await PreferenceCall<NativePreferences>(new { action = "read" });
        var form = new StackPanel { Spacing = 8 };
        ToggleSwitch Toggle(string label, bool enabled)
        {
            var toggle = new ToggleSwitch { Header = Tr(label), IsOn = enabled };
            form.Children.Add(toggle);
            return toggle;
        }
        TextBox Input(string label, string? value, bool multiline = false)
        {
            var field = new TextBox { Header = Tr(label), Text = value ?? "", AcceptsReturn = multiline, TextWrapping = TextWrapping.Wrap };
            form.Children.Add(field);
            return field;
        }
        void Heading(string label) => form.Children.Add(new TextBlock { Text = Tr(label), FontSize = 18, Margin = new Thickness(0, 8, 0, 0) });

        Heading("Вигляд бібліотеки");
        var option0 = Toggle("Компактна бібліотека", values.compact_instance_cards);
        var option1 = Toggle("Показувати час гри", values.show_play_time);
        var option2 = Toggle("Показувати швидкий перехід", values.show_jump_in);
        var option3 = Toggle("Приховувати встановлені збірки в каталозі", values.hide_installed_modpacks);
        Heading("Вкладки профілю");
        var option4 = Toggle("Показувати вкладку «Світи»", values.show_worlds_tab_in_instances);
        var option5 = Toggle("Показувати вкладку «Файли»", values.show_files_tab_in_instances);
        var option6 = Toggle("Показувати вкладку «Скриншоти»", values.show_screenshots_tab_in_instances);
        var option7 = Toggle("Показувати кнопку вибору скіна в бічній панелі", values.show_skin_selector_in_sidebar);
        Heading("Поведінка");
        var option8 = Toggle("Згортати лаунчер після запуску Minecraft", values.hide_on_process_start);
        var option9 = Toggle("Повертати вікно після виходу з Minecraft", values.refocus_on_game_close);
        var option10 = Toggle("Підтверджувати імпорт локальних модпаків", values.warn_on_unknown_modpacks);
        var option11 = Toggle("Показувати відомості для копіювання", values.always_show_copy_details);
        Heading("Розширений запуск");
        var env = Input("Змінні середовища · NAME=VALUE", string.Join(Environment.NewLine, values.custom_env_vars.Select(pair => string.Join("=", pair))), true);
        var pre = Input("Перед запуском", values.hooks.pre_launch);
        var wrapper = Input("Команда-обгортка", values.hooks.wrapper);
        var post = Input("Після виходу", values.hooks.post_exit);
        Heading("Сховище й діагностика");
        var downloads = Input("Одночасні завантаження", values.max_concurrent_downloads.ToString());
        var writes = Input("Одночасні записи", values.max_concurrent_writes.ToString());
        form.Children.Add(new TextBlock { Text = Tr("Ліміти застосовуються після перезапуску Gradient."), TextWrapping = TextWrapping.Wrap });
        if (await Editor(Tr("Налаштування"), new ScrollViewer { Content = form, MaxHeight = 550 }, Tr("Зберегти")).ShowAsync() != ContentDialogResult.Primary)
            return;

        var variables = env.Text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('=', 2)).ToArray();
        if (variables.Any(pair => pair.Length != 2) ||
            !int.TryParse(downloads.Text, out var downloadValue) || !int.TryParse(writes.Text, out var writeValue))
            throw new FormatException(Tr("Змінні середовища · NAME=VALUE"));
        static string? Command(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        var next = new NativePreferences(
            option0.IsOn,
            option1.IsOn,
            option2.IsOn,
            option3.IsOn,
            option4.IsOn,
            option5.IsOn,
            option6.IsOn,
            option7.IsOn,
            option8.IsOn,
            option9.IsOn,
            option10.IsOn,
            option11.IsOn,
            downloadValue, writeValue, variables,
            new LaunchHooks(Command(pre.Text), Command(wrapper.Text), Command(post.Text)));
        await PreferenceCall<JsonElement>(new { action = "save", values = next });
        ApplyPreferences(next);
    }, waitForCore: true);

    private async void OpenStorageSettings(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var locations = await PreferenceCall<DataLocations>(new { action = "locations" });
        var usage = await PreferenceCall<StorageUsage>(new { action = "store_usage" });
        var form = new StackPanel { Spacing = 8 };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var space = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var progress = new ProgressRing { Width = 22, Height = 22, HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
        var working = false;
        void ShowUsage(StorageUsage value) => space.Text = string.Join(Environment.NewLine,
            Tr("Унікальний контент · {0} МіБ", value.unique_bytes >> 20),
            Tr("Спільний контент · {0} МіБ", value.shared_bytes >> 20),
            Tr("Невикористано · {0} МіБ", value.unused_cache_bytes >> 20));
        void AddAction(string label, Func<Task> action, bool refreshUsage = false)
        {
            var button = new Button { Content = Tr(label) };
            button.Click += async (_, _) =>
            {
                if (working) return;
                working = true;
                progress.Visibility = Visibility.Visible;
                progress.IsActive = true;
                form.IsHitTestVisible = false;
                button.IsEnabled = false;
                status.Text = "";
                try
                {
                    await action();
                    if (refreshUsage) ShowUsage(await PreferenceCall<StorageUsage>(new { action = "store_usage" }));
                    if (status.Text.Length == 0) status.Text = Tr("Дію виконано.");
                }
                catch (Exception error) { status.Text = error.Message; }
                finally
                {
                    working = false;
                    progress.IsActive = false;
                    progress.Visibility = Visibility.Collapsed;
                    button.IsEnabled = true;
                    form.IsHitTestVisible = true;
                }
            };
            form.Children.Add(button);
        }
        var path = new TextBlock { Text = locations.data, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        form.Children.Add(path);
        AddAction("Відкрити папку даних", () => OpenLocation(locations.data));
        AddAction("Змінити папку даних…", async () =>
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder != null && await PreferenceCall<bool>(new { action = "move_directory", path = folder.Path }))
                status.Text = Tr("Нова папка застосовується після перезапуску Gradient.");
        });
        AddAction("Резервні копії бази", () => OpenLocation(locations.backups));
        ShowUsage(usage);
        form.Children.Add(space);
        var limit = new NumberBox { Header = Tr("Ліміт кешу (ГіБ)"), Minimum = 0, Maximum = 1048576,
            Value = usage.cache_limit_bytes / (double)(1UL << 30), SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        form.Children.Add(limit);
        AddAction("Зберегти ліміт", async () =>
        {
            if (!double.IsFinite(limit.Value)) throw new FormatException(Tr("Ліміт кешу (ГіБ)"));
            await PreferenceCall<JsonElement>(new { action = "store_limit", bytes = (ulong)(limit.Value * (1UL << 30)) });
        }, true);
        AddAction("Очистити невикористаний контент", async () => { await PreferenceCall<ulong>(new { action = "store_cleanup" }); }, true);
        AddAction("Перевірити й відновити файли", async () =>
        {
            var result = await PreferenceCall<StorageRepair>(new { action = "store_repair" });
            status.Text = Tr("Помилок під час перевірки: {0}", result.issues.Length);
        }, true);
        AddAction("Очистити кеш каталогу", async () => { await PreferenceCall<JsonElement>(new { action = "clear_cache" }); });
        form.Children.Add(new TextBlock { Text = Tr("Архів містить логи лаунчера й Minecraft."), TextWrapping = TextWrapping.Wrap });
        AddAction("Експорт діагностики…", async () =>
        {
            var picker = new FileSavePicker { SuggestedFileName = "gradient-diagnostics" };
            picker.FileTypeChoices.Add("ZIP", new[] { ".zip" });
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var file = await picker.PickSaveFileAsync();
            if (file != null) await PreferenceCall<bool>(new { action = "export_debug", path = file.Path });
        });
        form.Children.Add(progress);
        form.Children.Add(status);
        var dialog = new ContentDialog
        {
            Title = Tr("Сховище й діагностика"),
            Content = new ScrollViewer { Content = form, MaxHeight = 550 },
            CloseButtonText = Tr("Закрити"),
            XamlRoot = Layout.XamlRoot
        };
        dialog.Closing += (_, args) => args.Cancel = working;
        await dialog.ShowAsync();
    }, waitForCore: true);

    private static Task OpenLocation(string path)
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    private void RefreshCopyDetails()
    {
        var visibility = extraPreferences?.always_show_copy_details == true && !string.IsNullOrWhiteSpace(Status.Text)
            ? Visibility.Visible : Visibility.Collapsed;
        CopyLibraryDetails.Visibility = CopyProfileDetails.Visibility = visibility;
    }

    private void CopyStatusDetails(object sender, RoutedEventArgs e)
    {
        var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
        data.SetText(Status.Text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
    }

    private void ObserveGameWindows(Profile[] next)
    {
        if (extraPreferences is not { } preferences || AppWindow.Presenter is not OverlappedPresenter presenter) return;
        var wasPlaying = library.Any(profile => profile.running);
        var isPlaying = next.Any(profile => profile.running);
        if (isPlaying && !wasPlaying && preferences.hide_on_process_start) presenter.Minimize();
        if (!isPlaying && wasPlaying && preferences.refocus_on_game_close)
        {
            if (presenter.State == OverlappedPresenterState.Minimized) presenter.Restore();
            Activate();
        }
    }

    private void UseG1Preset(object sender, RoutedEventArgs e) => ArgumentsSetting.Text = "-XX:+UseG1GC";
    private void ResetJvmArguments(object sender, RoutedEventArgs e) => ArgumentsSetting.Text = "";
    private void UseShenandoahPreset(object sender, RoutedEventArgs e) => ArgumentsSetting.Text = "-XX:+UseShenandoahGC";
    private void UseZgcPreset(object sender, RoutedEventArgs e) => ArgumentsSetting.Text = "-XX:+UseZGC";
}
