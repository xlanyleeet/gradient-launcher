using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using static Gradient.InterfaceText;

namespace Gradient;

public sealed partial class MainWindow
{
    private sealed record ServerEntry(string id, string name, string address, bool editable);
    private string? serverViewKey;

    private async Task LoadServers(Profile profile)
    {
        var key = $"{profile.id}/{profile.running}/{profile.quarantined}";
        if (serverViewKey == key) return;
        AddServerButton.IsEnabled = false;
        ServerRows.Children.Clear();
        var servers = await core.Call<ServerEntry[]>(new { method = "servers", id = profile.id });
        if (SelectedProfile?.id != profile.id || !Equals(Details.SelectedItem, ServersTab)) return;
        AddServerButton.IsEnabled = !profile.running && !profile.quarantined;
        if (servers.Length == 0) ServerRows.Children.Add(new TextBlock { Text = Tr("Серверів ще немає.") });
        foreach (var server in servers)
        {
            var row = ProfileRow(server.name, server.address);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (!profile.running && !profile.quarantined)
            {
                var join = new Button { Content = Tr("Приєднатися") };
                join.Click += async (_, _) => await MutateProfile(profile,
                    new { method = "join_server", id = profile.id, server = server.id });
                actions.Children.Add(join);
                if (server.editable)
                {
                    var edit = new Button { Content = Tr("Редагувати") };
                    edit.Click += async (_, _) => await Run(() => EditServer(profile, server));
                    var remove = new Button { Content = Tr("Видалити") };
                    remove.Click += async (_, _) => await Run(async () =>
                    {
                        var dialog = Editor(Tr("Видалити сервер?"), server.name, Tr("Видалити"));
                        dialog.DefaultButton = ContentDialogButton.Close;
                        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                            await SaveServerChange(new { method = "remove_server", id = profile.id, server = server.id });
                    });
                    actions.Children.Add(edit);
                    actions.Children.Add(remove);
                }
            }
            row.Children.Add(actions);
            ServerRows.Children.Add(row);
        }
        serverViewKey = key;
    }

    private async void AddServer(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is { } profile) await Run(() => EditServer(profile, null));
    }

    private async Task EditServer(Profile profile, ServerEntry? server)
    {
        var name = new TextBox { Header = Tr("Назва"), Text = server?.name ?? "" };
        var address = new TextBox { Header = Tr("Адреса"), Text = server?.address ?? "" };
        var form = new StackPanel { Spacing = 10 };
        form.Children.Add(name);
        form.Children.Add(address);
        var dialog = Editor(server is null ? Tr("Додати сервер") : Tr("Редагувати сервер"), form, Tr("Зберегти"));
        void Validate() => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(name.Text) && !string.IsNullOrWhiteSpace(address.Text);
        name.TextChanged += (_, _) => Validate();
        address.TextChanged += (_, _) => Validate();
        Validate();
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await SaveServerChange(new { method = "save_server", id = profile.id, server = server?.id, name = name.Text, address = address.Text });
    }

    private async Task SaveServerChange(object request)
    {
        await core.Call<JsonElement>(request);
        serverViewKey = null;
        await Update();
    }
}
