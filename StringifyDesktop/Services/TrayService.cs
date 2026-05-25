using Avalonia;
using Avalonia.Controls;
using StringifyDesktop.Commands;

namespace StringifyDesktop.Services;

public sealed class TrayService : IDisposable
{
    private readonly TrayIcon trayIcon;
    
    public event EventHandler? OpenRequested;
    public event EventHandler? QuitRequested;

    public TrayService(AppPaths paths)
    {
        trayIcon = CreateTray(paths);
    }

    private TrayIcon CreateTray(AppPaths paths)
    {
        using var stream = paths.OpenAppIconStream();
        var windowIcon = new WindowIcon(stream);
        var openCommand = new RelayCommand(() => OpenRequested?.Invoke(this, EventArgs.Empty));
        var quitCommand = new RelayCommand(() => QuitRequested?.Invoke(this, EventArgs.Empty));

        var menu = new NativeMenu();
        menu.Items.Add(new NativeMenuItem
        {
            Header = "Open Stringify Desktop",
            Command = openCommand
        });
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(new NativeMenuItem
        {
            Header = "Quit",
            Command = quitCommand
        });

        var trayIconInstance = new TrayIcon
        {
            Icon = windowIcon,
            ToolTipText = "Stringify Desktop",
            Command = openCommand,
            Menu = menu
        };
        
        TrayIcon.SetIcons(Application.Current ?? throw new InvalidOperationException("Application is not initialized.")
            ,[trayIconInstance]);

        return trayIconInstance;
    }

    public void ShowSyncingNotification()
    {
        trayIcon.ToolTipText = "Still syncing from the tray";;
    }

    public void Dispose()
    {
        trayIcon.Dispose();
    }
}
