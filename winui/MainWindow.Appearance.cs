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
        var title = dark ? Color.FromArgb(255, 20, 31, 39) : Color.FromArgb(255, 224, 242, 240);
        var navigation = dark ? Color.FromArgb(255, 25, 38, 44) : Color.FromArgb(255, 209, 234, 231);
        if (new AccessibilitySettings().HighContrast)
            surface = title = navigation = new UISettings().GetColorValue(UIColorType.Background);

        Layout.Background = LibrarySurface.Background = new SolidColorBrush(surface);
        WindowTitleBar.Background = new SolidColorBrush(title);
        Navigation.Background = new SolidColorBrush(navigation);
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
