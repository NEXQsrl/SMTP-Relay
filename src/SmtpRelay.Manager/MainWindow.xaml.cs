using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using SmtpRelay.Manager.Services;
using SmtpRelay.Manager.ViewModels;

namespace SmtpRelay.Manager;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _editTimer;
    private readonly DispatcherTimer _statusTimer;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        DataContext = _vm;

        _vm.AllowedIps.CollectionChanged += (_, _) => UpdateEmptyIpText();
        UpdateEmptyIpText();
        _vm.SnmpManagers.CollectionChanged += (_, _) => UpdateEmptyManagerText();
        UpdateEmptyManagerText();

        // While the administrator types, keep the validation and the "unsaved changes" state current.
        _editTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _editTimer.Tick += (_, _) => _vm.Revalidate();

        // Service state, today's counters and the live log view.
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _statusTimer.Tick += (_, _) => RefreshStatus();

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_vm.IsDemo)
            return;

        _editTimer.Start();
        _statusTimer.Start();
        RefreshStatus();

        if (_vm.Options.Updates.CheckOnStartup)
            await _vm.CheckForUpdatesAsync();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_vm.IsDemo || !_vm.IsDirty)
            return;

        var answer = MessageBox.Show(this,
            "You have unsaved changes. Close the console and lose them?",
            Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
            e.Cancel = true;
    }

    private void RefreshStatus()
    {
        _vm.RefreshService();
        if (_vm.Page == "Overview")
            _vm.RefreshStats();
        if (_vm.Page == "Logs" && _vm.AutoRefreshLogs)
            _vm.RefreshLogFiles();
    }

    private void UpdateEmptyIpText()
        => EmptyIpText.Visibility = _vm.AllowedIps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateEmptyManagerText()
        => EmptyManagerText.Visibility = _vm.SnmpManagers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    // ----- helpers ---------------------------------------------------------------------------------------

    private async Task RunBusyAsync(string text, Func<Task> work)
    {
        BusyText.Text = text;
        Busy.Visibility = Visibility.Visible;
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            Busy.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowError(string message)
        => MessageBox.Show(this, message, Title, MessageBoxButton.OK, MessageBoxImage.Error);

    private void ShowInfo(string message)
        => MessageBox.Show(this, message, Title, MessageBoxButton.OK, MessageBoxImage.Information);

    private static void OpenExternal(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose(); }
        catch (Exception) { /* no browser or no association: nothing useful to do */ }
    }

    // ----- service ---------------------------------------------------------------------------------------

    private async void Start_Click(object sender, RoutedEventArgs e)
        => await RunBusyAsync("Starting the service…", _vm.StartServiceAsync);

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "Stop the relay? Devices and applications will not be able to send mail until it is started again.",
            Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes)
            await RunBusyAsync("Stopping the service…", _vm.StopServiceAsync);
    }

    private async void Restart_Click(object sender, RoutedEventArgs e)
        => await RunBusyAsync("Restarting the service…", _vm.RestartServiceAsync);

    private async void Install_Click(object sender, RoutedEventArgs e)
        => await RunBusyAsync("Installing the service…", async () =>
        {
            var (code, output) = await _vm.InstallServiceAsync();
            if (code != 0)
                ShowError("The service was not installed.\n\n" + output);
        });

    // ----- save ------------------------------------------------------------------------------------------

    private void Save_Click(object sender, RoutedEventArgs e) => TrySave();

    private bool TrySave()
    {
        try
        {
            if (_vm.Save())
                return true;
            ShowError("The settings were not saved:\n\n• " + string.Join("\n• ", _vm.Errors));
        }
        catch (Exception ex)
        {
            ShowError($"Cannot write {_vm.ConfigPath}:\n{ex.Message}");
        }
        return false;
    }

    private async void SaveRestart_Click(object sender, RoutedEventArgs e)
    {
        if (!TrySave())
            return;

        if (!_vm.IsServiceInstalled)
        {
            ShowInfo("The settings were saved. The service is not installed yet: install it from the Overview page.");
            return;
        }
        await RunBusyAsync("Restarting the service…", _vm.RestartServiceAsync);
    }

    private void Discard_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this, "Discard every change made since the last save?",
            Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes)
            _vm.Reload();
    }

    // ----- allowlist -------------------------------------------------------------------------------------

    private void AddIp_Click(object sender, RoutedEventArgs e) => AddIp();

    private void NewIpBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddIp();
            e.Handled = true;
        }
    }

    private void AddIp()
    {
        var error = _vm.AddAllowedIp(NewIpBox.Text);
        NewIpError.Text = error ?? "";
        NewIpError.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        if (error is null)
            NewIpBox.Clear();
        NewIpBox.Focus();
    }

    private void RemoveIp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string entry })
            _vm.RemoveAllowedIp(entry);
    }

    // ----- secrets ---------------------------------------------------------------------------------------

    private void StoreClientSecret_Click(object sender, RoutedEventArgs e)
        => StoreSecret(_vm.EntraApp.ClientSecretName, ClientSecretBox);

    private void StoreCommunity_Click(object sender, RoutedEventArgs e)
        => StoreSecret(_vm.Options.Snmp.CommunitySecret, CommunityBox);

    private void StorePassword_Click(object sender, RoutedEventArgs e)
        => StoreSecret(_vm.Options.Upstream.Basic.PasswordSecret, PasswordBoxBasic);

    private void StorePfxPassword_Click(object sender, RoutedEventArgs e)
        => StoreSecret(_vm.Options.Tls.CertificatePasswordSecret ?? "", PfxPasswordBox);

    private void StoreSecret(string name, PasswordBox box)
    {
        if (box.Password.Length == 0)
        {
            ShowInfo("Type the value first.");
            box.Focus();
            return;
        }

        try
        {
            _vm.SaveSecret(name, box.Password);
            box.Clear();
        }
        catch (Exception ex)
        {
            ShowError("The secret was not stored:\n" + ex.Message);
        }
    }

    private void BrowsePfx_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose the certificate",
            Filter = "PKCS#12 certificate (*.pfx;*.p12)|*.pfx;*.p12|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            PfxPathBox.Text = dialog.FileName;
            _vm.Revalidate();
        }
    }

    // ----- certificate of the app registration -----------------------------------------------------------

    private void CreateCertificate_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "Create a new self-signed certificate for the app registration?\n\n" +
            "It is stored in the computer store (Local Machine > Personal) with a private key that cannot be exported, " +
            "and is valid for two years. You then upload its public part (.cer) to the app registration in Microsoft Entra ID.",
            Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (answer != MessageBoxResult.Yes)
            return;

        try
        {
            using var certificate = _vm.CreateEntraCertificate();
            ExportCertificate();
        }
        catch (Exception ex)
        {
            ShowError("The certificate was not created:\n" + ex.Message);
        }
    }

    private void ExportCertificate_Click(object sender, RoutedEventArgs e) => ExportCertificate();

    private void ExportCertificate()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save the public certificate to upload to the app registration",
            Filter = "Certificate (*.cer)|*.cer",
            FileName = "SmtpRelay-" + Environment.MachineName + ".cer",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            _vm.ExportEntraCertificate(dialog.FileName);
            ShowInfo("Public certificate saved:\n" + dialog.FileName + "\n\n" +
                     "Upload it to the app registration: Microsoft Entra ID > App registrations > your application > " +
                     "Certificates & secrets > Certificates > Upload certificate.");
        }
        catch (Exception ex)
        {
            ShowError("The certificate was not exported:\n" + ex.Message);
        }
    }

    // ----- SNMP ------------------------------------------------------------------------------------------

    private void AddManager_Click(object sender, RoutedEventArgs e) => AddManager();

    private void NewManagerBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddManager();
            e.Handled = true;
        }
    }

    private void AddManager()
    {
        var error = _vm.AddSnmpManager(NewManagerBox.Text);
        NewManagerError.Text = error ?? "";
        NewManagerError.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        if (error is null)
            NewManagerBox.Clear();
        NewManagerBox.Focus();
    }

    private void RemoveManager_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string entry })
            _vm.RemoveSnmpManager(entry);
    }

    private void SaveMib_Click(object sender, RoutedEventArgs e)
    {
        var source = MainViewModel.MibFilePath;
        if (source is null)
        {
            ShowInfo("The MIB file is not installed next to the console. Download it from the release page on GitHub.");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Save the MIB file",
            Filter = "MIB module (*.txt;*.mib)|*.txt;*.mib|All files (*.*)|*.*",
            FileName = Path.GetFileName(source),
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            File.Copy(source, dialog.FileName, overwrite: true);
        }
        catch (Exception ex)
        {
            ShowError("The MIB file was not saved:\n" + ex.Message);
        }
    }

    private void CopyOid_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string text })
        {
            try { Clipboard.SetText(text); }
            catch (Exception) { /* the clipboard is busy: nothing useful to do */ }
        }
    }

    // ----- tests -----------------------------------------------------------------------------------------

    private void ShowTestResult(bool ok, string text)
    {
        TestResult.Background = (Brush)FindResource(ok ? "OkSoft" : "DangerSoft");
        TestResultText.Foreground = (Brush)FindResource(ok ? "Ok" : "Danger");
        TestResultText.Text = text;
        TestResult.Visibility = Visibility.Visible;
    }

    private static string Describe(Exception ex)
        => ex.InnerException is null || ex.Message.Contains(ex.InnerException.Message, StringComparison.Ordinal)
            ? ex.Message
            : ex.Message + "\n" + ex.InnerException.Message;

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        _vm.Revalidate();
        if (_vm.HasErrors)
        {
            ShowTestResult(false, "Fix the settings first:\n• " + string.Join("\n• ", _vm.Errors));
            return;
        }

        TestButton.IsEnabled = TestMailButton.IsEnabled = false;
        ShowTestResult(true, $"Connecting to {_vm.Options.Upstream.Host}:{_vm.Options.Upstream.Port}…");
        try
        {
            await _vm.TestConnectionAsync();
            ShowTestResult(true, _vm.IsAnonymous
                ? "Connection succeeded."
                : "Connection and authentication succeeded.");
        }
        catch (Exception ex)
        {
            ShowTestResult(false, "The test failed: " + Describe(ex));
        }
        finally
        {
            TestButton.IsEnabled = TestMailButton.IsEnabled = true;
        }
    }

    private async void SendTest_Click(object sender, RoutedEventArgs e)
    {
        _vm.Revalidate();
        if (_vm.HasErrors)
        {
            ShowTestResult(false, "Fix the settings first:\n• " + string.Join("\n• ", _vm.Errors));
            return;
        }

        var dialog = new InputDialog(this, "Send a test message",
            "A short message is sent straight to the smart host with the settings on this page.",
            ("From", _vm.DefaultTestSender), ("To", ""));
        if (dialog.ShowDialog() != true)
            return;

        string from = dialog[0], to = dialog[1];
        if (!from.Contains('@') || !to.Contains('@'))
        {
            ShowTestResult(false, "Type a valid sender and a valid recipient address.");
            return;
        }

        TestButton.IsEnabled = TestMailButton.IsEnabled = false;
        ShowTestResult(true, $"Sending a test message to {to}…");
        try
        {
            await _vm.SendTestMessageAsync(from, to);
            ShowTestResult(true, $"The smart host accepted the test message for {to}.");
        }
        catch (Exception ex)
        {
            ShowTestResult(false, "The message was not sent: " + Describe(ex));
        }
        finally
        {
            TestButton.IsEnabled = TestMailButton.IsEnabled = true;
        }
    }

    // ----- logs ------------------------------------------------------------------------------------------

    private void RefreshLogs_Click(object sender, RoutedEventArgs e) => _vm.RefreshLogFiles();

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        var directory = _vm.Options.LogDirectory;
        if (Directory.Exists(directory))
            OpenExternal(directory);
        else
            ShowInfo($"The log folder does not exist yet:\n{directory}");
    }

    private void OpenLogsPage_Click(object sender, RoutedEventArgs e)
    {
        _vm.Page = "Logs";
        _vm.RefreshLogFiles();
    }

    private void LogBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Live view: keep the newest lines in sight.
        if (_vm.AutoRefreshLogs)
            LogBox.ScrollToEnd();
    }

    // ----- updates and links -----------------------------------------------------------------------------

    private void UpdateBadge_Click(object sender, RoutedEventArgs e) => _vm.Page = "About";

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await _vm.CheckForUpdatesAsync();

    private void OpenReleasePage_Click(object sender, RoutedEventArgs e)
        => OpenExternal(_vm.LatestRelease?.PageUrl is { } url && url.StartsWith(AppInfo.RepositoryUrl, StringComparison.OrdinalIgnoreCase)
            ? url
            : AppInfo.ReleasesUrl);

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        var release = _vm.LatestRelease;
        if (release is null)
            return;

        if (_vm.IsDirty)
        {
            ShowInfo("Save or discard your changes before installing the update.");
            return;
        }

        var answer = MessageBox.Show(this,
            $"Install version {release.Tag.TrimStart('v', 'V')} now?\n\n" +
            "The relay stops for about a minute while the files are replaced, then starts again. " +
            "Settings, secrets and logs are kept. This console closes and reopens when the update is done.",
            Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (answer != MessageBoxResult.Yes)
            return;

        try
        {
            var path = await _vm.DownloadUpdateAsync();
            UpdateService.LaunchInstaller(path);
            ManagerLog.Write($"Setup of {release.Tag} started; the console is closing.");
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            ShowError("The update was not installed.\n\n" + ex.Message);
        }
    }

    private void OpenRepository_Click(object sender, RoutedEventArgs e) => OpenExternal(AppInfo.RepositoryUrl);
    private void OpenWebsite_Click(object sender, RoutedEventArgs e) => OpenExternal(AppInfo.WebsiteUrl);

    private void OpenLicense_Click(object sender, RoutedEventArgs e)
    {
        var local = Path.Combine(AppContext.BaseDirectory, "LICENSE.txt");
        OpenExternal(File.Exists(local) ? local : AppInfo.RepositoryUrl + "/blob/master/LICENSE");
    }
}
