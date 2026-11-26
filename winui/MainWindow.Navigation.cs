using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using System.Text.Json;
using static Gradient.InterfaceText;

namespace Gradient;

public sealed partial class MainWindow
{
    private void ToggleNavigationPane(object sender, RoutedEventArgs args) =>
        Navigation.IsPaneOpen = !Navigation.IsPaneOpen;

    private bool showAllProfiles;
    public sealed record QuickEntry(string id, string profile, string name, bool server, string ProfileLabel = "");

    private void ShowMoreProfiles()
    {
        showAllProfiles = !showAllProfiles;
        FilterProfiles();
    }

    private void HomeSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (ready && Search.Text != sender.Text) Search.Text = sender.Text;
    }

    private async Task LoadQuickPlay()
    {
        if (extraPreferences?.show_jump_in == false)
        {
            QuickSection.Visibility = Visibility.Collapsed;
            return;
        }
        var entries = await core.Call<QuickEntry[]>(new { method = "quick_play" });
        QuickPlayCards.ItemsSource = entries.Select(entry => entry with
        {
            ProfileLabel = library.FirstOrDefault(profile => profile.id == entry.profile)?.name ?? ""
        }).ToArray();
        QuickSection.Visibility = entries.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OpenQuickPlay(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is not QuickEntry entry) return;
        await Run(async () =>
        {
            var request = entry.server
                ? (object)new { method = "join_server", id = entry.profile, server = entry.id }
                : new { method = "open_world", id = entry.profile, world = entry.id };
            Status.Text = Tr("Готуємо Minecraft…");
            await core.Call<JsonElement>(request);
            Status.Text = "";
            await Update();
        }, waitForCore: true);
    }

    private async void Navigate(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (Equals(args.InvokedItemContainer, DiscoverNavigation))
            await OpenDiscover();
        else if (Equals(args.InvokedItemContainer, SettingsNavigation))
            await OpenSettings();
        else if (Equals(args.InvokedItemContainer, SkinNavigation))
            ManageSkin(sender, null!);
        else if (Equals(args.InvokedItemContainer, WhatsNewNavigation))
            await ShowWhatsNew();
        else if (Equals(args.InvokedItemContainer, AddProfileNavigation))
            FlyoutBase.ShowAttachedFlyout(AddProfileNavigation);
        else if (Equals(args.InvokedItemContainer, MoreProfilesNavigation))
            ShowMoreProfiles();
        else if (args.InvokedItemContainer?.Tag is string id &&
                 library.FirstOrDefault(profile => profile.id == id) is { } profile)
            await OpenProfileDetails(profile);
        else if (Equals(args.InvokedItemContainer, LibraryNavigation))
        {
            ShowLibrary();
            await Run(LoadQuickPlay, waitForCore: true);
        }
    }

    private async Task ShowWhatsNew()
    {
        var notes = new StackPanel { Spacing = 10, MaxWidth = 440 };
        foreach (var text in new[]
        {
            Tr("Шифрування токенів акаунтів (DPAPI / Keychain) та очищення токенів у резервних копіях бази."),
            Tr("Масове оновлення модів («Оновити все»), оновлення модпаків Modrinth, пошук і фільтри у вкладці «Контент»."),
            Tr("Додавання локальних ресурспаків, шейдерів і датапаків (.zip), а також вибір версії завантажувача та Snapshots."),
            Tr("Автопошук встановлених Java та підказка рекомендованої оперативної пам’яті."),
        })
            notes.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        notes.Children.Add(new HyperlinkButton
        {
            Content = Tr("Релізи на GitHub"),
            NavigateUri = new Uri("https://github.com/xlanyleeet/gradient-launcher/releases")
        });
        await new ContentDialog
        {
            RequestedTheme = Layout.ActualTheme,
            XamlRoot = Layout.XamlRoot,
            Title = Tr("Що нового"),
            Content = notes,
            CloseButtonText = Tr("Закрити"),
        }.ShowAsync();
    }

    private async void OpenLibraryProfile(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is Profile profile) await OpenProfileDetails(profile);
    }

    private async Task OpenProfileDetails(Profile profile)
    {
        if (selectedProfileId == profile.id && LibraryPage.Visibility == Visibility.Visible) return;
        ShowProfile(profile.id);
        await Run(LoadSelected, waitForCore: true);
    }

    private void ShowProfile(string id)
    {
        var changed = selectedProfileId != id;
        selectedProfileId = id;
        SettingsPage.Visibility = DiscoverPage.Visibility = LibraryHome.Visibility = ProjectPage.Visibility = Visibility.Collapsed;
        LibraryPage.Visibility = Visibility.Visible;
        BackButton.Visibility = Visibility.Visible;
        if (changed)
        {
            Details.SelectedItem = ContentTab;
            ContentSearch.Text = "";
            ContentKindFilter.SelectedIndex = 0;
        }
        FilterProfiles();
    }

    private async void BackToLibrary(object sender, RoutedEventArgs args)
    {
        if (ProjectPage.Visibility == Visibility.Visible)
        {
            ShowDiscoverPage();
            return;
        }
        ShowLibrary();
        await Run(LoadQuickPlay, waitForCore: true);
    }

    private void SubmitProfileSearch(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        ShowLibrary();
    }

    private void ShowLibrary()
    {
        SettingsPage.Visibility = Visibility.Collapsed;
        DiscoverPage.Visibility = Visibility.Collapsed;
        ProjectPage.Visibility = Visibility.Collapsed;
        LibraryPage.Visibility = Visibility.Collapsed;
        LibraryHome.Visibility = Visibility.Visible;
        BackButton.Visibility = Visibility.Collapsed;
        Navigation.SelectedItem = LibraryNavigation;
    }
}
