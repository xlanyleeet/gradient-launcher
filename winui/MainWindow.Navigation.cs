using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Gradient;

public sealed partial class MainWindow
{
    private void ToggleNavigationPane(TitleBar sender, object args) =>
        Navigation.IsPaneOpen = !Navigation.IsPaneOpen;

    private void ShowProfileList(NavigationView sender, object args) =>
        ProfilePane.Visibility = Visibility.Visible;

    private void HideProfileList(NavigationView sender, NavigationViewPaneClosingEventArgs args) =>
        ProfilePane.Visibility = Visibility.Collapsed;

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
        }
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
        LibraryPage.Visibility = Visibility.Visible;
        Navigation.SelectedItem = LibraryNavigation;
    }
}
