using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;
using static Gradient.InterfaceText;

namespace Gradient;

public sealed partial class MainWindow
{
    private InterfacePreferences appearance = InterfacePreferences.Load();
    private int SidebarLimit => Math.Clamp(appearance.SidebarProfiles, 1, 30);

    private void TitleBarRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => ResizeTitleBarSearch();

    private void ResizeTitleBarSearch()
    {
        if (Layout.XamlRoot is not { } root) return;
        var captionInset = (AppWindow.TitleBar.LeftInset + AppWindow.TitleBar.RightInset) / root.RasterizationScale;
        // Leave room for the menu, back button, app icon, account and window drag area.
        var available = Layout.ActualWidth - captionInset - 240;
        Search.Width = Math.Clamp(available * 0.70, 0, 760);
    }

    private void InitializeAppearance()
    {
        ThemeSetting.SelectedIndex = appearance.Theme switch { "light" => 1, "dark" => 2, _ => 0 };
        LanguageSetting.SelectedIndex = appearance.Language == "en" ? 1 : 0;
        SidebarLimitSetting.Value = SidebarLimit;
        Layout.ActualThemeChanged += (_, _) => ApplyPalette();
        ApplyTheme();
    }

    private void ApplyTheme()
    {
        Layout.RequestedTheme = appearance.Theme switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        ApplyPalette();
    }

    private void ApplyPalette()
    {
        var dark = Layout.ActualTheme == ElementTheme.Dark;
        var surface = dark ? Color.FromArgb(255, 23, 46, 50) : Color.FromArgb(255, 168, 222, 215);
        var chrome = dark ? Color.FromArgb(255, 20, 31, 39) : Color.FromArgb(255, 224, 242, 240);
        if (new AccessibilitySettings().HighContrast)
            surface = chrome = new UISettings().GetColorValue(UIColorType.Background);

        Layout.Background = LibrarySurface.Background = new SolidColorBrush(surface);
        WindowTitleBar.Background = Navigation.Background = new SolidColorBrush(chrome);
        OverlayPaneBrush.Color = ExpandedPaneBrush.Color = chrome;
        CoverClear.Color = Color.FromArgb(0, surface.R, surface.G, surface.B);
        CoverFade.Color = Color.FromArgb(179, surface.R, surface.G, surface.B);
        CoverSolid.Color = surface;
    }

    private void ChangeAppearance(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || ThemeSetting.SelectedItem is not ComboBoxItem theme ||
            LanguageSetting.SelectedItem is not ComboBoxItem language) return;

        var next = appearance with { Theme = (string)theme.Tag, Language = (string)language.Tag };
        try
        {
            next.Save();
            appearance = next;
            ApplyTheme();
            AppearanceStatus.Text = appearance.Language != InterfaceText.Language
                ? Tr("Перезапустіть Gradient, щоб змінити мову.")
                : "";
        }
        catch (Exception error) { AppearanceStatus.Text = error.Message; }
    }

    private void ChangeSidebarLimit(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!ready || double.IsNaN(args.NewValue)) return;
        try
        {
            var next = appearance with { SidebarProfiles = Math.Clamp((int)args.NewValue, 1, 30) };
            next.Save();
            appearance = next;
            showAllProfiles = false;
            FilterProfiles();
        }
        catch (Exception error) { AppearanceStatus.Text = error.Message; }
    }
}
