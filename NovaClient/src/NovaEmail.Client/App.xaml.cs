using Microsoft.UI.Xaml;

namespace NovaEmail.Client;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        UnhandledException += (_, eventArgs) => LogStartupFailure(eventArgs.Exception);
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception exception)
        {
            LogStartupFailure(exception);
            throw;
        }
    }

    internal static void LogStartupFailure(Exception exception)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "NovaEmail-startup.log"),
                $"{DateTimeOffset.UtcNow:O}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Startup diagnostics must never replace the original exception.
        }
    }
}
