using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using FluentAvalonia.Styling;
using QueueCache.Desktop.Services;
using QueueCache.Desktop.ViewModels;
using QueueCache.Desktop.Views;
using QueueCache.Operations.ManagedDisks;

namespace QueueCache.Desktop;

public sealed class App : Application
{
    private ShellViewModel? shell;
    private MainWindow? window;
    private TrayIcon? tray;
    private NativeMenuItem? trayStatus;
    private bool holdingShutdown;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (OperatingSystem.IsWindows() && ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var dialogs = new DialogService(() => window);
            shell = new ShellViewModel(new WindowsCacheTaskService(), new WindowsManagedDiskService(), dialogs, new DesktopSettingsStore(DesktopSettingsStore.DefaultPath),
                signIn: new WindowsSignInTask());
            ApplyTheme(shell.Settings.Current.Theme);
            shell.Settings.Changed += (_, settings) => ApplyTheme(settings.Theme);
            // The window may hide to the notification area; the app ends only on Exit or a real close.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            window = new MainWindow { DataContext = shell };
            window.Closed += (_, _) => desktop.Shutdown();
            // Started at sign-in (--tray): only the notification-area icon until the window is asked for.
            if (!Program.InTray)
                desktop.MainWindow = window;
            CreateTrayIcon(desktop);
            desktop.ShutdownRequested += (_, e) => SaveBeforeShutdown(desktop, e);
            _ = shell.StartAsync();
            if (Program.Instance is { } instance)
                instance.ShowRequested += () => Dispatcher.UIThread.Post(ShowWindow);
        }
        base.OnFrameworkInitializationCompleted();
    }

    public static ThemeVariant Variant(AppTheme theme) => theme switch
    {
        AppTheme.Light => ThemeVariant.Light,
        AppTheme.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default
    };

    // "Use the Windows setting" lets FluentAvalonia follow Windows (including high contrast); an
    // explicit Light or Dark choice must not be overridden when Windows changes. Only called on Windows:
    // elsewhere (the headless tests on Linux) system detection shells out to gsettings.
    private void ApplyTheme(AppTheme theme)
    {
        Styles.OfType<FluentAvaloniaTheme>().Single().PreferSystemTheme = theme == AppTheme.System;
        RequestedThemeVariant = Variant(theme);
    }

    private void CreateTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        using var icon = AssetLoader.Open(new Uri("avares://QueueCache.Desktop/Assets/queuecache.ico"));
        trayStatus = new NativeMenuItem("Checking caches and disks…") { IsEnabled = false };
        var open = new NativeMenuItem("Open QueueCache");
        open.Click += (_, _) => ShowWindow();
        var flush = new NativeMenuItem("Write all pending data to disk") { Command = shell!.FlushAllCommand };
        var save = new NativeMenuItem("Save all unsaved disks") { Command = shell.SaveAllCommand };
        var exit = new NativeMenuItem("Exit QueueCache");
        exit.Click += (_, _) => window?.Exit();
        tray = new TrayIcon
        {
            Icon = new WindowIcon(icon),
            ToolTipText = "QueueCache",
            Menu = new NativeMenu { Items = { open, new NativeMenuItemSeparator(), trayStatus, flush, save, new NativeMenuItemSeparator(), exit } }
        };
        tray.Clicked += (_, _) => ShowWindow();
        TrayIcon.SetIcons(this, [tray]);
        shell.Monitor.Updated += (_, _) =>
        {
            var summary = shell.Monitor.HealthTitle;
            trayStatus.Header = summary;
            // Windows shows at most 127 characters.
            var text = "QueueCache · " + summary + (shell.Monitor.ActivitySummary is { } activity ? "\n" + activity : "");
            tray.ToolTipText = text.Length > 127 ? text[..127] : text;
        };
        desktop.Exit += (_, _) => tray.IsVisible = false;
    }

    /// <summary>Windows is shutting down (or signing out): save the images that save at shutdown now, while
    /// Windows can still show why it waits (#22). The app then exits, and Windows goes on by itself.</summary>
    private void SaveBeforeShutdown(IClassicDesktopStyleApplicationLifetime desktop, ShutdownRequestedEventArgs e)
    {
        // On Windows only the end of the session raises this; Exit shuts the app down without asking.
        if (shell is null || window is null || !OperatingSystem.IsWindows())
            return;
        if (holdingShutdown)
        {
            e.Cancel = true;
            return;
        }
        var pending = ShutdownSaves.Pending(shell.Monitor.VirtualDisks);
        if (pending.Count == 0 || window.TryGetPlatformHandle()?.Handle is not { } handle)
            return;
        e.Cancel = true;
        holdingShutdown = true;
        ShowWindow();
        ShutdownSaves.Hold(handle, ShutdownSaves.Reason(pending[0]));
        _ = SaveThenExitAsync(desktop, shell.Monitor, pending, handle);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task SaveThenExitAsync(IClassicDesktopStyleApplicationLifetime desktop, DashboardMonitor monitor, IReadOnlyList<VirtualDiskViewModel> pending, IntPtr handle)
    {
        try
        {
            foreach (var disk in pending)
            {
                var reason = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                reason.Tick += (_, _) => ShutdownSaves.Hold(handle, ShutdownSaves.Reason(disk));
                reason.Start();
                try { await monitor.RunDiskAsync(disk, "Saving", ManagedDiskAction.Save, atShutdown: true); }
                finally { reason.Stop(); }
            }
        }
        finally { ShutdownSaves.Release(handle); }
        // A save that failed is tried again by the QueueCache service while Windows shuts down.
        desktop.Shutdown();
    }

    private void ShowWindow()
    {
        if (window is null)
            return;
        window.Show();
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
    }
}
