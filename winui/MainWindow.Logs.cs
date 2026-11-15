using Microsoft.UI.Xaml.Controls;

using static Gradient.InterfaceText;

namespace Gradient;

public sealed partial class MainWindow
{
    public sealed record ArchivedLog(string filename, string log_type)
    {
        public string Label => log_type switch
        {
            "Live" => Tr("Живий лог"),
            "CrashReport" => Tr("{0} · Звіт про збій", filename),
            _ => filename
        };
    }

    private static readonly ArchivedLog liveLog = new("", "Live");
    private string? logProfileId;
    private ArchivedLog? displayedLog;

    private async void LogChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ready && SelectedProfile is { } profile && Equals(Details.SelectedItem, LogTab))
            await Run(() => LoadLog(profile));
    }

    private async Task LoadLog(Profile profile)
    {
        var key = $"{profile.id}/{profile.running}";
        if (logProfileId != key)
        {
            var previous = logProfileId?.StartsWith(profile.id + "/", StringComparison.Ordinal) == true
                ? LogPicker.SelectedItem as ArchivedLog : null;
            var logs = await core.Call<ArchivedLog[]>(new { method = "log_history", id = profile.id });
            if (SelectedProfile?.id != profile.id || !Equals(Details.SelectedItem, LogTab)) return;
            LogPicker.ItemsSource = logs.Prepend(liveLog).ToArray();
            LogPicker.SelectedItem = logs.FirstOrDefault(log => log == previous) ?? liveLog;
            LogPicker.IsEnabled = true;
            logProfileId = key;
            displayedLog = null;
        }

        var source = LogPicker.SelectedItem as ArchivedLog ?? liveLog;
        if (source != liveLog && source == displayedLog) return;
        var output = source == liveLog
            ? await core.Call<string>(new { method = "log", id = profile.id })
            : await core.Call<string>(new { method = "read_log", id = profile.id, kind = source.log_type, filename = source.filename });
        if (SelectedProfile?.id == profile.id && Equals(LogPicker.SelectedItem, source))
        {
            GameLog.Text = string.IsNullOrWhiteSpace(output) ? Tr("Лог порожній.") : output;
            displayedLog = source;
        }
    }
}
