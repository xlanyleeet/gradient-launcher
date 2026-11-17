using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Input;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;
using Windows.UI.ViewManagement;
using static Gradient.InterfaceText;

namespace Gradient;

public sealed partial class MainWindow
{
    private InterfacePreferences appearance = InterfacePreferences.Load();
    private int SidebarLimit => Math.Clamp(appearance.SidebarProfiles, 1, 30);

    private RectInt32[] titleBarInputRegions = [];

    private void UpdateTitleBar()
    {
        if (closed || Layout.XamlRoot is not { } root) return;
        var scale = root.RasterizationScale;
        var left = AppWindow.TitleBar.LeftInset / scale;
        var right = AppWindow.TitleBar.RightInset / scale;
        if (CaptionLeftSpace.Width.Value != left) CaptionLeftSpace.Width = new GridLength(left);
        if (CaptionRightSpace.Width.Value != right) CaptionRightSpace.Width = new GridLength(right);

        var regions = new List<RectInt32>(4);
        foreach (var element in new FrameworkElement[] { PaneButton, BackButton, Search, AccountsButton })
        {
            if (element.Visibility != Visibility.Visible || element.ActualWidth <= 0 || element.ActualHeight <= 0) continue;
            var bounds = element.TransformToVisual(Layout).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            var x = (int)Math.Ceiling(Math.Max(left, bounds.Left) * scale);
            var end = (int)Math.Floor(Math.Min(Layout.ActualWidth - right, bounds.Right) * scale);
            if (end <= x) continue;
            regions.Add(new RectInt32(x, (int)Math.Floor(bounds.Top * scale), end - x,
                (int)Math.Ceiling(bounds.Bottom * scale) - (int)Math.Floor(bounds.Top * scale)));
        }
        if (titleBarInputRegions.SequenceEqual(regions)) return;
        titleBarInputRegions = regions.ToArray();
        InputNonClientPointerSource.GetForWindowId(AppWindow.Id)
            .SetRegionRects(NonClientRegionKind.Passthrough, titleBarInputRegions);
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
