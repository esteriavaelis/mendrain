using Mendrain.Core;
using Mendrain.Helpers;
using Mendrain.ViewModels;
using Microsoft.UI.Xaml;

namespace Mendrain;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
        DebugSettings.XamlResourceReferenceFailed += (_, e) => Log("XAML resource: " + e.Message);
        DebugSettings.BindingFailed += (_, e) => Log("Binding: " + e.Message);
    }

    /// <summary>Appends a diagnostic line to %LOCALAPPDATA%\Mendrain\mendrain.log.</summary>
    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataFolder);
            File.AppendAllText(Path.Combine(AppSettings.DataFolder, "mendrain.log"), $"{DateTime.Now:u} {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static MainWindow MainWindow { get; private set; } = null!;
    public static nint WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(MainWindow);
    public static AppSettings Settings { get; } = new();

    // Long-lived view models keep their state (and running work) while the user navigates around.
    public static BatchExtractViewModel Batch { get; } = new();
    public static CreateZipViewModel CreateZip { get; } = new();
    public static UpdateViewModel Updates { get; } = new();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        MainWindow.Activate();

        // "Mendrain.exe --shell <action> <file>" comes from the Explorer context menu (see ShellMenu).
        var commandLine = Environment.GetCommandLineArgs().Skip(1).ToList();
        var command = ShellCommand.Parse(commandLine);
        if (command is not null)
            ShellRequests.Add(command);
        ShellRequests.Start(MainWindow.DispatcherQueue);
        // Once per session, in the background; never blocks the window or interrupts extraction.
        _ = Updates.CheckOnStartupAsync();
        if (command is not null)
            return;

        // "Mendrain.exe <archive>" (e.g. Open with) opens that archive directly.
        var paths = commandLine.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        if (paths.Count > 0)
            MainWindow.HandleIncomingPaths(paths);
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // Keep the window alive for unexpected UI errors and tell the user instead of vanishing.
        Log("Unhandled: " + e.Exception);
        e.Handled = true;
        _ = Dialogs.MessageAsync("Something went wrong", e.Exception?.Message ?? e.Message);
    }
}
