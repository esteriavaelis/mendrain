using Mendrain.Core;
using Mendrain.Helpers;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;

namespace Mendrain;

/// <summary>
/// Entry point (replaces the XAML-generated Main). Runs Velopack's install/update hooks, handles the
/// register/unregister switches without any UI, and hands Explorer requests (context menu, and opening an associated
/// archive) to an already running Mendrain instead of opening another window.
/// </summary>
public static class Program
{
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(15);

    [STAThread]
    private static int Main(string[] args)
    {
        // Must come first: Velopack starts the exe with special arguments for its install/update/uninstall
        // hooks, runs the matching callback here and exits. The hooks may not show UI.
        VelopackApp.Build()
            // An update is applied only when the user clicks "Restart now", never silently at startup.
            .SetAutoApplyOnStartup(false)
            .OnAfterInstallFastCallback(_ =>
            {
                ShellMenu.Register(Environment.ProcessPath!);
                // Lists Mendrain in Open with / Default apps; the user's default archive app stays as it is.
                FileAssociations.Register(Environment.ProcessPath!);
            })
            .OnAfterUpdateFastCallback(_ =>
            {
                ShellMenu.RepairAfterUpdate(Environment.ProcessPath!);
                FileAssociations.RepairAfterUpdate(Environment.ProcessPath!);
            })
            .OnBeforeUninstallFastCallback(_ =>
            {
                ShellMenu.Unregister();
                FileAssociations.Unregister();
            })
            .Run();

        if ((ShellMenu.HandleCommandLine(args) ?? FileAssociations.HandleCommandLine(args)) is { } exitCode)
            return exitCode;

        var isMain = InstanceChannel.TryBecomeMain();
        if (!isMain && ShellCommand.Parse(args) is not null)
        {
            if (InstanceChannel.TrySend(args, SendTimeout))
                return 0;
            // The main instance did not answer (it may have just closed): handle the request here instead.
            isMain = InstanceChannel.TryBecomeMain();
        }
        // The first Mendrain process receives Explorer requests from later ones. A normal start while
        // Mendrain is already open still opens its own window, as before.
        if (isMain)
            InstanceChannel.StartServer(request =>
            {
                if (ShellCommand.Parse(request) is { } command)
                    ShellRequests.Add(command);
            });

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
        return 0;
    }
}
