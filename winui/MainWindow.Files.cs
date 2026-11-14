using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Gradient;

public sealed partial class MainWindow
{
    public sealed record FileEntry(string name, string path, string type, ulong? size, bool readOnly)
    {
        public string Label => type == "directory" ? $"▸  {name}" : name;
    }

    private string? fileProfile;
    private string directoryPath = "";

    private async Task LoadDirectory(Profile profile, string path)
    {
        var entries = await core.Call<FileEntry[]>(new { method = "files", id = profile.id, path });
        if (SelectedProfile?.id != profile.id || !Equals(Details.SelectedItem, FilesTab)) return;
        FileList.ItemsSource = entries;
        DirectoryLabel.Text = entries.Length == 0 ? $"/{path} · Порожня папка" : $"/{path}";
        ParentDirectoryButton.IsEnabled = path.Length > 0;
        fileProfile = profile.id;
        directoryPath = path;
    }

    private async void ParentDirectory(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is not { } profile || fileProfile != profile.id) return;
        int separator = directoryPath.LastIndexOf('/');
        await Run(() => LoadDirectory(profile, separator < 0 ? "" : directoryPath[..separator]));
    }

    private async void OpenFileEntry(object sender, ItemClickEventArgs e)
    {
        if (SelectedProfile is not { } profile || fileProfile != profile.id || e.ClickedItem is not FileEntry entry) return;
        await Run(async () =>
        {
            if (entry.type == "directory")
            {
                await LoadDirectory(profile, entry.path);
                return;
            }
            if (entry.type != "file") return;

            string preview;
            bool editable = !entry.readOnly && !profile.running && !profile.quarantined;
            try { preview = await core.Call<string>(new { method = "preview_file", id = profile.id, path = entry.path }); }
            catch (InvalidOperationException error) { preview = error.Message; editable = false; }
            var text = new TextBox
            {
                Text = preview, IsReadOnly = !editable, AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap, MinHeight = 125, MaxHeight = 390
            };
            var dialog = Editor(entry.name, text, editable ? "Зберегти зміни" : "Зберегти копію");
            if (editable) dialog.SecondaryButtonText = "Зберегти копію";
            var result = await dialog.ShowAsync();
            if (editable && result == ContentDialogResult.Primary)
            {
                await core.Call<JsonElement>(new { method = "save_profile_text", id = profile.id,
                    path = entry.path, before = preview, text = text.Text });
                Status.Text = "Файл збережено.";
                return;
            }
            if (result != (editable ? ContentDialogResult.Secondary : ContentDialogResult.Primary)) return;

            var picker = new FileSavePicker { SuggestedFileName = entry.name };
            picker.FileTypeChoices.Add("Файл", new[] { "*" });
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var destination = await picker.PickSaveFileAsync();
            if (destination is null) return;
            await core.Call<JsonElement>(new { method = "copy_file", id = profile.id, path = entry.path, destination = destination.Path });
            Status.Text = "Копію файлу збережено.";
        });
    }
}
