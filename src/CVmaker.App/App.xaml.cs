using System.IO;
using System.Windows;
using System.Windows.Threading;
using CVmaker.App.Localization;
using CVmaker.App.Services;

namespace CVmaker.App;

public partial class App : Application
{
    public static string LogDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CVmaker");

    public static UserSettings Settings { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => WriteCrash(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => { WriteCrash(args.Exception); args.SetObserved(); };

        Settings = UserSettings.Load();
        Loc.Apply(Settings.Language);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrash(e.Exception);
        MessageBox.Show("Unexpected error:\n" + e.Exception.Message + "\n\nDetails were written to " + Path.Combine(LogDirectory, "crash.log"),
            "CVmaker", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    public static void WriteCrash(Exception? ex)
    {
        if (ex == null) return;
        try
        {
            Directory.CreateDirectory(LogDirectory);
            File.AppendAllText(Path.Combine(LogDirectory, "crash.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\r\n\r\n");
        }
        catch { /* nothing else we can do */ }
    }
}
