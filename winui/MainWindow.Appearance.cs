using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Gradient.InterfaceText;

namespace Gradient;

public sealed partial class MainWindow
{
    private InterfacePreferences appearance = InterfacePreferences.Load();

    private void InitializeAppearance()
    {
        ThemeSetting.SelectedIndex = appearance.Theme switch { "light" => 1, "dark" => 2, _ => 0 };
        LanguageSetting.SelectedIndex = appearance.Language == "en" ? 1 : 0;
        ApplyTheme();
    }

    private void ApplyTheme() => Layout.RequestedTheme = appearance.Theme switch
    {
        "light" => ElementTheme.Light,
        "dark" => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    private void ChangeAppearance(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || ThemeSetting.SelectedItem is not ComboBoxItem theme ||
            LanguageSetting.SelectedItem is not ComboBoxItem language) return;

        var next = new InterfacePreferences((string)theme.Tag, (string)language.Tag);
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
}
