using System.IO;
using System.Windows;
using System.Windows.Threading;
using SmtpRelay.Manager.Services;
using SmtpRelay.Manager.ViewModels;

namespace SmtpRelay.Manager;

public partial class App : Application
{
    private ConsoleLock? _consoleLock;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // English interface on every Windows language: numbers are typed and shown the same way everywhere.
        var culture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        DispatcherUnhandledException += OnUnhandledException;

#if DEBUG
        // Development only: renders every page with sample data to PNG files (used for the documentation).
        if (e.Args.Length == 2 && e.Args[0] == "--screenshots")
        {
            try
            {
                Screenshots.Render(e.Args[1]);
                Shutdown(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "smtprelay-screenshots-error.txt"), ex.ToString());
                Shutdown(1);
            }
            return;
        }
#endif

        MainViewModel viewModel;
        try
        {
            viewModel = new MainViewModel(AppContext.BaseDirectory);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"The settings file cannot be read:\n{Path.Combine(AppContext.BaseDirectory, "appsettings.json")}\n\n{ex.Message}",
                AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        // One administrator at a time changes the settings; a second console opens read-only.
        _consoleLock = new ConsoleLock(viewModel.Options.DataDirectory);
        if (_consoleLock.TryAcquire())
        {
            ManagerLog.Write($"Console {AppInfo.Version} opened.");
        }
        else
        {
            var holder = _consoleLock.ReadHolder();
            viewModel.SetReadOnly(true, holder?.User, holder?.SinceUtc);
            ManagerLog.Write($"Console {AppInfo.Version} opened read-only by {ConsoleLock.CurrentUser}: {holder?.User ?? "another administrator"} has it open for editing.");
        }

        MainWindow = new MainWindow(viewModel, _consoleLock);
        MainWindow.Show();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ManagerLog.Write("Unexpected error: " + e.Exception);
        MessageBox.Show("Unexpected error:\n\n" + e.Exception.Message, AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _consoleLock?.Dispose();
        base.OnExit(e);
    }
}
