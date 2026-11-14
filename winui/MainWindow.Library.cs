using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Gradient;

public sealed partial class MainWindow
{
    public sealed record Account(string id, string name, bool selected)
    {
        public string Label => selected ? $"{name} · Основний" : name;
    }

    private ContentDialog Editor(string title, object content, string action) => new()
    {
        XamlRoot = Layout.XamlRoot,
        Title = title,
        Content = content,
        PrimaryButtonText = action,
        CloseButtonText = "Скасувати",
        DefaultButton = ContentDialogButton.Primary
    };

    public sealed record Group(string id, string name, bool selected);

    private async void OpenProfileFolder(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (SelectedProfile is not { } profile) return;
        var path = await core.Call<string>(new { method = "profile_folder", id = profile.id });
        var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(path);
        if (!await Windows.System.Launcher.LaunchFolderAsync(folder))
            Status.Text = "Не вдалося відкрити папку профілю.";
    });

    private async void ProfileGroups(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (SelectedProfile is not { } profile) return;
        var groups = await core.Call<Group[]>(new { method = "groups", id = profile.id });
        var form = new StackPanel { Spacing = 8 };
        foreach (var group in groups)
            form.Children.Add(new CheckBox { Content = group.name, Tag = group.id, IsChecked = group.selected });
        var name = new TextBox { Header = "Нова група", PlaceholderText = "Назва групи" };
        form.Children.Add(name);
        var dialog = Editor("Групи профілю", new ScrollViewer { Content = form, MaxHeight = 380 }, "Зберегти");
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
        var versions = await core.Call<string[]>(new { method = "game_versions" });
        var name = new TextBox { Header = "Назва", PlaceholderText = "Мій профіль" };
        var version = new ComboBox { Header = "Minecraft", ItemsSource = versions, SelectedIndex = versions.Length == 0 ? -1 : 0 };
        var loaders = new[] { "Vanilla", "Fabric", "Forge", "NeoForge", "Quilt" };
        var loader = new ComboBox { Header = "Завантажувач", ItemsSource = loaders, SelectedIndex = 0 };
        var form = new StackPanel { Spacing = 12 };
        form.Children.Add(name);
        form.Children.Add(version);
        form.Children.Add(loader);
        var dialog = Editor("Новий профіль", form, "Створити");
        void Validate() => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(name.Text) && version.SelectedItem is string;
        name.TextChanged += (_, _) => Validate();
        version.SelectionChanged += (_, _) => Validate();
        Validate();
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await ApplyProfileChange(new
        {
            method = "create_profile", name = name.Text, version = (string)version.SelectedItem,
            loader = ((string)loader.SelectedItem).ToLowerInvariant()
        }, "Профіль додано до черги.");
    });

    private async void RenameProfile(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (SelectedProfile is not { } profile) return;
        var name = new TextBox { Header = "Назва", Text = profile.name };
        var dialog = Editor("Перейменувати профіль", name, "Зберегти");
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
                "Дублювання додано до черги.");
    }

    private async void RemoveProfile(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (SelectedProfile is not { } profile) return;
        var dialog = Editor("Видалити профіль?", $"«{profile.name}» і його локальні файли буде видалено.", "Видалити");
        dialog.DefaultButton = ContentDialogButton.Close;
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await ApplyProfileChange(new { method = "remove_profile", id = profile.id });
    });

    private async void ManageAccounts(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var accounts = await core.Call<Account[]>(new { method = "accounts" });
        if (accounts.Length == 0)
        {
            Status.Text = "Акаунтів ще немає. Увійдіть у Microsoft або додайте офлайн-акаунт.";
            return;
        }

        var list = new ListView { ItemsSource = accounts, DisplayMemberPath = "Label",
            SelectedItem = accounts.FirstOrDefault(account => account.selected) ?? accounts[0], MaxHeight = 290 };
        var dialog = Editor("Акаунти Minecraft", list, "Зробити основним");
        dialog.SecondaryButtonText = "Видалити";
        var result = await dialog.ShowAsync();
        if (list.SelectedItem is not Account account) return;
        if (result == ContentDialogResult.Primary)
            await ApplyProfileChange(new { method = "use_account", id = account.id }, $"Основний акаунт · {account.name}");
        if (result == ContentDialogResult.Secondary)
        {
            var confirm = Editor("Видалити акаунт?", account.name, "Видалити");
            confirm.DefaultButton = ContentDialogButton.Close;
            if (await confirm.ShowAsync() == ContentDialogResult.Primary)
                await ApplyProfileChange(new { method = "remove_account", id = account.id });
        }
    });
}
