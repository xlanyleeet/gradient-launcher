using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

using static Gradient.InterfaceText;

namespace Gradient;

public sealed partial class MainWindow
{
    public sealed record Account(string id, string name, bool selected, string? avatar_url, string? avatar_png = null)
    {
        public string Label => selected ? Tr("{0} · Основний", name) : name;
    }

    private ContentDialog Editor(string title, object content, string action) => new()
    {
        RequestedTheme = Layout.ActualTheme,
        XamlRoot = Layout.XamlRoot,
        Title = title,
        Content = content,
        PrimaryButtonText = action,
        CloseButtonText = Tr("Скасувати"),
        DefaultButton = ContentDialogButton.Primary
    };

    public sealed record Group(string id, string name, bool selected);
    private sealed record ShortcutProfile(string id, string name, bool quarantined);

    private async void CreateProfileShortcut(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (SelectedProfile is not { } profile) return;
        var link = await core.Call<string>(new { method = "create_profile_shortcut", id = profile.id });
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.Desktop,
            SuggestedFileName = "Gradient Minecraft" };
        picker.FileTypeChoices.Add(Tr("Ярлик Gradient"), new[] { ".url" });
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var destination = await picker.PickSaveFileAsync();
        if (destination is null) return;
        RegisterGradientScheme();
        await Windows.Storage.FileIO.WriteTextAsync(destination, $"[InternetShortcut]\r\nURL={link}\r\n");
        Status.Text = Tr("Ярлик профілю збережено.");
    }, waitForCore: true);

    private Task OpenProfileShortcut(string url) => Run(async () =>
    {
        ShowLibrary();
        var target = await core.Call<ShortcutProfile>(new { method = "resolve_profile_shortcut", url });
        Search.Text = "";
        await Update();
        var profile = library.FirstOrDefault(entry => entry.id == target.id);
        if (profile is not null)
        {
            ShowProfile(profile.id);
            FilterProfiles();
            await LoadSelected();
        }
        if (target.quarantined || profile?.running == true)
        {
            Status.Text = target.quarantined ? Tr("Цей профіль заблоковано.") : Tr("Minecraft уже запущено.");
            return;
        }
        var dialog = Editor(Tr("Запустити Minecraft?"), target.name, Tr("Грати"));
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await ApplyProfileChange(new { method = "play", id = target.id });
    }, waitForCore: true);

    private sealed record SkinPreview(string preview, string variant);

    private async void ManageSkin(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var model = new ComboBox { Header = Tr("Модель"), ItemsSource = new[] { Tr("Класична"), Tr("Тонкі руки") }, SelectedIndex = 0 };
        var image = new Image { Width = 92, Height = 184, HorizontalAlignment = HorizontalAlignment.Center };
        var notice = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var choose = new Button { Content = Tr("Вибрати PNG") };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(image);
        panel.Children.Add(model);
        panel.Children.Add(choose);
        panel.Children.Add(notice);
        var dialog = Editor(Tr("Скін активного Minecraft-акаунта"), panel, Tr("Застосувати"));
        dialog.SecondaryButtonText = Tr("Скинути скін і плащ");
        dialog.IsPrimaryButtonEnabled = false;
        string? path = null;
        string Variant() => model.SelectedIndex == 1 ? "slim" : "classic";

        async Task LoadDraft()
        {
            if (path is null) return;
            dialog.IsPrimaryButtonEnabled = dialog.IsSecondaryButtonEnabled = model.IsEnabled = choose.IsEnabled = false;
            try
            {
                var draft = await core.Call<SkinPreview>(new { method = "preview_skin", path, variant = Variant() });
                await SetSkinPreview(image, draft);
                notice.Text = Path.GetFileName(path);
                dialog.IsPrimaryButtonEnabled = true;
            }
            catch (Exception error) { image.Source = null; notice.Text = error.Message; }
            finally { dialog.IsSecondaryButtonEnabled = model.IsEnabled = choose.IsEnabled = true; }
        }

        try
        {
            var active = await core.Call<SkinPreview?>(new { method = "current_skin_preview" });
            if (active is not null)
            {
                model.SelectedIndex = active.variant == "slim" ? 1 : 0;
                await SetSkinPreview(image, active);
            }
        }
        catch (Exception error) { notice.Text = error.Message; }

        model.SelectionChanged += async (_, _) => await LoadDraft();
        choose.Click += async (_, _) =>
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".png");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            path = file.Path;
            await LoadDraft();
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && path is not null)
        {
            await core.Call<JsonElement>(new { method = "upload_skin", path, variant = Variant() });
            await RefreshAccountAvatar();
            Status.Text = Tr("Скін Minecraft застосовано.");
        }
        else if (result == ContentDialogResult.Secondary)
        {
            var confirm = Editor(Tr("Скинути скін і плащ?"), Tr("Minecraft повернеться до стандартного скіна."), Tr("Скинути"));
            confirm.DefaultButton = ContentDialogButton.Close;
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
            await core.Call<JsonElement>(new { method = "reset_skin" });
            await RefreshAccountAvatar();
            Status.Text = Tr("Скін Minecraft скинуто.");
        }
    }, waitForCore: true);

    private static Task SetSkinPreview(Image image, SkinPreview preview) => SetPngSource(image, preview.preview);

    private static async Task SetPngSource(Image image, string png)
    {
        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        using var writer = new Windows.Storage.Streams.DataWriter(stream);
        writer.WriteBytes(Convert.FromBase64String(png));
        await writer.StoreAsync();
        writer.DetachStream();
        stream.Seek(0);
        var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
        await bitmap.SetSourceAsync(stream);
        image.Source = bitmap;
    }

    private async void OpenProfileFolder(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (SelectedProfile is not { } profile) return;
        var path = await core.Call<string>(new { method = "profile_folder", id = profile.id });
        var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(path);
        if (!await Windows.System.Launcher.LaunchFolderAsync(folder))
            Status.Text = Tr("Не вдалося відкрити папку профілю.");
    });

    private async void ProfileGroups(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (SelectedProfile is not { } profile) return;
        var groups = await core.Call<Group[]>(new { method = "groups", id = profile.id });
        var form = new StackPanel { Spacing = 8 };
        foreach (var group in groups)
            form.Children.Add(new CheckBox { Content = group.name, Tag = group.id, IsChecked = group.selected });
        var name = new TextBox { Header = Tr("Нова група"), PlaceholderText = Tr("Назва групи") };
        form.Children.Add(name);
        var dialog = Editor(Tr("Групи профілю"), new ScrollViewer { Content = form, MaxHeight = 380 }, Tr("Зберегти"));
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var selected = form.Children.OfType<CheckBox>().Where(group => group.IsChecked == true)
            .Select(group => (string)group.Tag).ToList();
        if (!string.IsNullOrWhiteSpace(name.Text))
        {
            var group = await core.Call<Group>(new { method = "create_group", name = name.Text });
            selected.Add(group.id);
        }
        await ApplyProfileChange(new { method = "set_groups", id = profile.id, groups = selected });
    });

    private async void CreateProfile(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        ShowLibrary();
        var versions = await core.Call<string[]>(new { method = "game_versions", include_snapshots = false });
        var name = new TextBox { Header = Tr("Назва"), PlaceholderText = Tr("Мій профіль") };
        var snapshots = new CheckBox { Content = Tr("Показувати знімки (Snapshots)") };
        var version = new ComboBox { Header = "Minecraft", ItemsSource = versions, SelectedIndex = versions.Length == 0 ? -1 : 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var loaders = new[] { "Vanilla", "Fabric", "Forge", "NeoForge", "Quilt" };
        var loader = new ComboBox { Header = Tr("Завантажувач"), ItemsSource = loaders, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var loaderVersion = new ComboBox
        {
            Header = Tr("Версія завантажувача"),
            ItemsSource = new[] { Tr("Автоматично") },
            SelectedIndex = 0,
            IsEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var form = new StackPanel { Spacing = 12 };
        form.Children.Add(name);
        form.Children.Add(snapshots);
        form.Children.Add(version);
        form.Children.Add(loader);
        form.Children.Add(loaderVersion);
        var dialog = Editor(Tr("Новий профіль"), form, Tr("Створити"));
        void Validate() => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(name.Text) && version.SelectedItem is string;

        async Task ReloadLoaderVersions()
        {
            var selectedLoader = ((string)loader.SelectedItem).ToLowerInvariant();
            var selectedGame = version.SelectedItem as string;
            if (selectedLoader == "vanilla" || string.IsNullOrEmpty(selectedGame))
            {
                loaderVersion.ItemsSource = new[] { Tr("Автоматично") };
                loaderVersion.SelectedIndex = 0;
                loaderVersion.IsEnabled = false;
                return;
            }
            loaderVersion.IsEnabled = false;
            try
            {
                var items = await core.Call<string[]>(new { method = "loader_versions", loader = selectedLoader, game_version = selectedGame });
                loaderVersion.ItemsSource = items.Prepend(Tr("Автоматично")).ToArray();
                loaderVersion.SelectedIndex = 0;
                loaderVersion.IsEnabled = true;
            }
            catch (Exception error)
            {
                loaderVersion.ItemsSource = new[] { Tr("Автоматично") };
                loaderVersion.SelectedIndex = 0;
                Status.Text = error.Message;
            }
        }

        async Task ReloadGameVersions()
        {
            try
            {
                var current = version.SelectedItem as string;
                var list = await core.Call<string[]>(new { method = "game_versions", include_snapshots = snapshots.IsChecked == true });
                version.ItemsSource = list;
                version.SelectedItem = current is not null && list.Contains(current) ? current : list.FirstOrDefault();
                Validate();
                await ReloadLoaderVersions();
            }
            catch (Exception error) { Status.Text = error.Message; }
        }

        name.TextChanged += (_, _) => Validate();
        snapshots.Checked += async (_, _) => await ReloadGameVersions();
        snapshots.Unchecked += async (_, _) => await ReloadGameVersions();
        version.SelectionChanged += async (_, _) =>
        {
            Validate();
            await ReloadLoaderVersions();
        };
        loader.SelectionChanged += async (_, _) => await ReloadLoaderVersions();
        Validate();
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var chosenLoaderVersion = loaderVersion.SelectedIndex > 0 ? loaderVersion.SelectedItem as string : null;
        await ApplyProfileChange(new
        {
            method = "create_profile", name = name.Text, version = (string)version.SelectedItem,
            loader = ((string)loader.SelectedItem).ToLowerInvariant(),
            loader_version = chosenLoaderVersion
        }, Tr("Профіль додано до черги."));
    });

    private async void RenameProfile(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (SelectedProfile is not { } profile) return;
        var name = new TextBox { Header = Tr("Назва"), Text = profile.name };
        var dialog = Editor(Tr("Перейменувати профіль"), name, Tr("Зберегти"));
        name.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(name.Text);
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await ApplyProfileChange(new { method = "rename_profile", id = profile.id, name = name.Text });
    });

    private async void ChangeProfileIcon(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (SelectedProfile is not { } profile) return;
        var picker = new FileOpenPicker();
        foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".webp" })
            picker.FileTypeFilter.Add(extension);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is not null)
            await ApplyProfileChange(new { method = "profile_icon", id = profile.id, path = file.Path });
    });

    private async void DuplicateProfile(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is { } profile)
            await MutateProfile(profile, new { method = "duplicate_profile", id = profile.id },
                Tr("Дублювання додано до черги."));
    }

    private async void RemoveProfile(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (SelectedProfile is not { } profile) return;
        var dialog = Editor(Tr("Видалити профіль?"), Tr("«{0}» і його локальні файли буде видалено.", profile.name), Tr("Видалити"));
        dialog.DefaultButton = ContentDialogButton.Close;
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await ApplyProfileChange(new { method = "remove_profile", id = profile.id });
    });

    private async Task RefreshAccountAvatar()
    {
        var accounts = await core.Call<Account[]>(new { method = "accounts" });
        var active = accounts.FirstOrDefault(account => account.selected);
        if (active?.avatar_png is { } png)
            await SetPngSource(AccountAvatar, png);
        else
            AccountAvatar.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(
                Uri.TryCreate(active?.avatar_url, UriKind.Absolute, out var avatar) && avatar.Scheme == Uri.UriSchemeHttps
                    ? avatar : DefaultAccountAvatar);
        var label = active is null ? Tr("Акаунти Minecraft") : Tr("Акаунти Minecraft · {0}", active.name);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(AccountsButton, label);
        ToolTipService.SetToolTip(AccountsButton, label);
    }

    private static readonly Uri DefaultAccountAvatar = new("ms-appx:///Assets/PlayerHead.png");

    private void AccountAvatarFailed(object sender, ExceptionRoutedEventArgs args)
    {
        if (AccountAvatar.Source is Microsoft.UI.Xaml.Media.Imaging.BitmapImage source && source.UriSource != DefaultAccountAvatar)
            AccountAvatar.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(DefaultAccountAvatar);
    }

    private async void ManageAccounts(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var accounts = await core.Call<Account[]>(new { method = "accounts" });
        if (accounts.Length == 0)
        {
            Status.Text = Tr("Акаунтів ще немає. Увійдіть у Microsoft або додайте офлайн-акаунт.");
            return;
        }

        var list = new ListView { ItemsSource = accounts, DisplayMemberPath = "Label",
            SelectedItem = accounts.FirstOrDefault(account => account.selected) ?? accounts[0], MaxHeight = 290 };
        var dialog = Editor(Tr("Акаунти Minecraft"), list, Tr("Зробити основним"));
        dialog.SecondaryButtonText = Tr("Видалити");
        var result = await dialog.ShowAsync();
        if (list.SelectedItem is not Account account) return;
        if (result == ContentDialogResult.Primary)
        {
            await core.Call<JsonElement>(new { method = "use_account", id = account.id });
            await RefreshAccountAvatar();
            Status.Text = Tr("Основний акаунт · {0}", account.name);
        }
        if (result == ContentDialogResult.Secondary)
        {
            var confirm = Editor(Tr("Видалити акаунт?"), account.name, Tr("Видалити"));
            confirm.DefaultButton = ContentDialogButton.Close;
            if (await confirm.ShowAsync() == ContentDialogResult.Primary)
            {
                await core.Call<JsonElement>(new { method = "remove_account", id = account.id });
                await RefreshAccountAvatar();
            }
        }
    });
}
