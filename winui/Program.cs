using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;

namespace Gradient;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(initialization =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            try
            {
                _ = new App();
            }
            catch (Exception error)
            {
                WriteStartupError(error);
                throw;
            }
        });
    }

    internal static void WriteStartupError(Exception error)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GradientNative");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "winui-error.log"), error.ToString());
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
