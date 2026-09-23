using System.IO;
using System.Windows;
using System.Windows.Threading;
using CVmania.App.Localization;
using CVmania.App.Services;

namespace CVmania.App;

public partial class App : Application
{
    public const string DisplayName = "CV!mania";

    public static string LogDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CVmania");

    public static UserSettings Settings { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => WriteCrash(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => { WriteCrash(args.Exception); args.SetObserved(); };

        MigrateOldSettings();
        Settings = UserSettings.Load();
        Loc.Apply(Settings.Language);
    }

    /// <summary>Versions before 1.1 were called CVmaker and kept their settings in %LOCALAPPDATA%\CVmaker.</summary>
    private static void MigrateOldSettings()
    {
        try
        {
            var old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CVmaker", "settings.json");
            if (File.Exists(old) && !File.Exists(UserSettings.FilePath))
            {
                Directory.CreateDirectory(LogDirectory);
                File.Copy(old, UserSettings.FilePath);
            }
        }
        catch { /* start with defaults */ }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrash(e.Exception);
        MessageBox.Show("Unexpected error:\n" + e.Exception.Message + "\n\nDetails were written to " + Path.Combine(LogDirectory, "crash.log"),
            DisplayName, MessageBoxButton.OK, MessageBoxImage.Error);
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
