using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Gradient;

public partial class App : Application
{
    private MainWindow? window;
    private AppInstance? primaryInstance;
    public App() => InitializeComponent();

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var current = AppInstance.GetCurrent();
        var activation = current.GetActivatedEventArgs();
        var primary = AppInstance.FindOrRegisterForKey("GradientLauncher");
        if (!primary.IsCurrent)
        {
            await primary.RedirectActivationToAsync(activation);
            Exit();
            return;
        }
        var mainWindow = new MainWindow();
        window = mainWindow;
        primaryInstance = primary;
        primaryInstance.Activated += (_, incoming) =>
        {
            mainWindow.DispatcherQueue.TryEnqueue(async () =>
            {
                mainWindow.Activate();
                await HandleActivation(mainWindow, incoming);
            });
        };
        mainWindow.Activate();
        if (await HandleActivation(mainWindow, activation)) return;
        if (Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(MainWindow.IsModpackPath) is { } path)
        {
            await mainWindow.ReceiveModpack(path);
            return;
        }
        var link = Environment.GetCommandLineArgs().Skip(1)
            .FirstOrDefault(arg => Uri.TryCreate(arg, UriKind.Absolute, out var uri)
                && uri.Scheme is "gradient-native" or "gradient" or "modrinth" or "https");
        if (link is not null)
            await mainWindow.ReceiveProjectLink(link);
    }

    private static string? ProjectLink(AppActivationArguments args) =>
        args.Kind == ExtendedActivationKind.Protocol &&
        args.Data is Windows.ApplicationModel.Activation.IProtocolActivatedEventArgs protocol
            ? protocol.Uri.AbsoluteUri : null;

    private static async Task<bool> HandleActivation(MainWindow window, AppActivationArguments args)
    {
        if (ProjectLink(args) is { } link)
        {
            await window.ReceiveProjectLink(link);
            return true;
        }
        if (args.Kind == ExtendedActivationKind.File &&
            args.Data is Windows.ApplicationModel.Activation.IFileActivatedEventArgs files)
        {
            foreach (var file in files.Files)
                await window.ReceiveModpack(file.Path);
            return true;
        }
        if (args.Kind == ExtendedActivationKind.Launch &&
            args.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch)
        {
            var path = launch.Arguments.Trim().Trim('"');
            if (MainWindow.IsModpackPath(path))
            {
                await window.ReceiveModpack(path);
                return true;
            }
        }
        return false;
    }
}
