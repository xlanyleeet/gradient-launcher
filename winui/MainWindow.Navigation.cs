using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json;
using static Gradient.InterfaceText;

namespace Gradient;

public sealed partial class MainWindow
{
    private void ToggleNavigationPane(TitleBar sender, object args) =>
        Navigation.IsPaneOpen = !Navigation.IsPaneOpen;

    private void ShowProfileList(NavigationView sender, object args) =>
        SetPaneExpanded(true);

    private void HideProfileList(NavigationView sender, NavigationViewPaneClosingEventArgs args) =>
        SetPaneExpanded(false);

    private void SetPaneExpanded(bool expanded) =>
        ProfilePane.Visibility = AccountNickname.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;

    private bool showAllProfiles;
    public sealed record QuickEntry(string id, string profile, string name, bool server, string ProfileLabel = "");

    private void ShowMoreProfiles(object sender, RoutedEventArgs args)
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
        else if (Equals(args.InvokedItemContainer, LibraryNavigation))
        {
            ShowLibrary();
            Navigation.IsPaneOpen = true;
            await Run(LoadQuickPlay, waitForCore: true);
        }
    }

    private async void OpenSidebarProfile(object sender, ItemClickEventArgs args)
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
        WindowTitleBar.IsBackButtonVisible = true;
        Navigation.SelectedItem = LibraryNavigation;
        if (changed) Details.SelectedItem = ContentTab;
        FilterProfiles();
    }

    private async void BackToLibrary(TitleBar sender, object args)
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
        Navigation.IsPaneOpen = true;
    }

    private void ShowLibrary()
    {
        SettingsPage.Visibility = Visibility.Collapsed;
        DiscoverPage.Visibility = Visibility.Collapsed;
        ProjectPage.Visibility = Visibility.Collapsed;
        LibraryPage.Visibility = Visibility.Collapsed;
        LibraryHome.Visibility = Visibility.Visible;
        WindowTitleBar.IsBackButtonVisible = false;
        Navigation.SelectedItem = LibraryNavigation;
    }
}
