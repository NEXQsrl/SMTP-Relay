using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using SmtpRelay.Configuration;
using SmtpRelay.Manager.Services;
using SmtpRelay.Manager.ViewModels;

namespace SmtpRelay.Manager;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _editTimer;
    private readonly DispatcherTimer _statusTimer;
    private int _statusTicks;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        DataContext = _vm;

        _vm.AllowedIps.CollectionChanged += (_, _) => UpdateEmptyIpText();
        UpdateEmptyIpText();
        _vm.SnmpManagers.CollectionChanged += (_, _) => UpdateEmptyManagerText();
        UpdateEmptyManagerText();
        _vm.Users.CollectionChanged += (_, _) => UpdateEmptyUserText();
        UpdateEmptyUserText();

        // While the administrator types, keep the validation and the "unsaved changes" state current.
        _editTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _editTimer.Tick += (_, _) => _vm.Revalidate();

        // Service state, today's counters and the live log view.
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _statusTimer.Tick += (_, _) => RefreshStatus();

        // Pages whose content is read from disk are filled when they are opened.
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Page))
                RefreshPageData(force: true);
        };

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void RefreshPageData(bool force)
    {
        if (_vm.IsDemo)
            return;
        try
        {
            if (_vm.Page == "Access" && (force || _statusTicks % 10 == 0))
                await _vm.RefreshSenderActivityAsync();
            if (_vm.Page == "Undelivered" && (force || _statusTicks % 3 == 0))
                _vm.RefreshUndelivered();
        }
        catch (Exception)
        {
            // The figures are a convenience: a file that cannot be read now is read at the next refresh.
        }
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
        if (_vm.Page == "Access")
            _vm.RefreshUsers(); // a user may have been changed with the command line
        _statusTicks++;
        RefreshPageData(force: false);
        if (_vm.Page == "Logs" && _vm.AutoRefreshLogs)
            _vm.RefreshLogFiles();
    }

    private void UpdateEmptyIpText()
        => EmptyIpText.Visibility = _vm.AllowedIps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateEmptyUserText()
        => EmptyUserText.Visibility = _vm.Users.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

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

    private void ImportIis_Click(object sender, RoutedEventArgs e)
    {
        SmtpRelay.Migration.Iis6RelayImport? import;
        try
        {
            import = MainViewModel.ReadIis6RelayList();
        }
        catch (Exception ex)
        {
            var raw = ex.Data[SmtpRelay.Migration.Iis6RelayList.RawValueKey] as string;
            ManagerLog.Write($"Import from IIS 6 failed: {ex.Message}{(raw is null ? "" : " " + raw)}");
            ShowError("The relay list of IIS could not be read:\n" + ex.Message +
                      (raw is null ? "" : "\n\nThe value found in IIS was written to the console log (Logs page, manager-*.log): send that line with your report."));
            return;
        }

        if (import is null)
        {
            ShowInfo("The IIS 6 SMTP service was not found on this computer.\n\n" +
                     "Open this console on the server where the IIS SMTP virtual server is installed, or type the addresses by hand.");
            return;
        }

        var notes = "";
        if (import.SkippedNames > 0)
            notes += $"\n\n{import.SkippedNames} entr{(import.SkippedNames == 1 ? "y is a domain name" : "ies are domain names")} and cannot be imported: the allowlist takes IP addresses only.";

        if (import.Entries.Count == 0)
        {
            ShowInfo((import.AllExceptList
                         ? "In IIS the relay restrictions are set to \"All except the list below\": there is no list of allowed computers to import."
                         : "In IIS the list of computers that may relay is empty: there is nothing to import.") + notes);
            return;
        }

        var fresh = import.Entries.Where(entry => !_vm.AllowedIps.Contains(entry, StringComparer.OrdinalIgnoreCase)).ToList();
        if (fresh.Count == 0)
        {
            ShowInfo($"All the {import.Entries.Count} computers allowed to relay in IIS are already in the list." + notes);
            return;
        }

        var shown = string.Join("\n", fresh.Take(20).Select(entry => "    " + entry));
        if (fresh.Count > 20)
            shown += $"\n    … and {fresh.Count - 20} more";
        var answer = MessageBox.Show(this,
            $"IIS allows {import.Entries.Count} computer{(import.Entries.Count == 1 ? "" : "s")} or network{(import.Entries.Count == 1 ? "" : "s")} to relay; " +
            $"{fresh.Count} {(fresh.Count == 1 ? "is" : "are")} not in the list yet:\n\n{shown}\n\nAdd {(fresh.Count == 1 ? "it" : "them")} to the allowlist?" + notes,
            Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (answer != MessageBoxResult.Yes)
            return;

        int added = _vm.ImportAllowedIps(fresh, import.Source);
        ShowInfo($"{added} entr{(added == 1 ? "y" : "ies")} added. Nothing in IIS was changed.\n\nSave the settings and restart the service to apply them.");
    }

    // ----- undelivered messages --------------------------------------------------------------------------

    private void RefreshUndelivered_Click(object sender, RoutedEventArgs e) => _vm.RefreshUndelivered();

    private void OpenSpoolFolder_Click(object sender, RoutedEventArgs e)
    {
        var directory = _vm.Options.FailedSpoolDirectory;
        if (Directory.Exists(directory))
            OpenExternal(directory);
        else
            ShowInfo($"The folder does not exist yet: no message has failed so far.\n{directory}");
    }

    private void OpenUndelivered_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: UndeliveredRow row } && File.Exists(row.Message.Path))
            OpenExternal(row.Message.Path);
    }

    private bool SettingsAllowResend()
    {
        _vm.Revalidate();
        if (!_vm.HasErrors)
            return true;
        ShowError("Fix the settings first:\n\n• " + string.Join("\n• ", _vm.Errors));
        return false;
    }

    private async void ResendOne_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: UndeliveredRow row } || !SettingsAllowResend())
            return;

        var answer = MessageBox.Show(this,
            $"Send this message again?\n\nSubject: {row.Subject}\nFrom: {row.From}\nTo: {row.To}\n\n" +
            "If the device that sent it has already tried again, the recipient receives it twice.",
            Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (answer != MessageBoxResult.Yes)
            return;

        await RunBusyAsync("Sending the message…", async () =>
        {
            try
            {
                await _vm.ResendAsync(row);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("The message was not sent and stays in the list:\n" + Describe(ex), ex);
            }
        });
        _vm.RefreshUndelivered();
    }

    private async void ResendAll_Click(object sender, RoutedEventArgs e)
    {
        var rows = _vm.Undelivered.ToList();
        if (rows.Count == 0 || !SettingsAllowResend())
            return;

        var answer = MessageBox.Show(this,
            $"Send all the {rows.Count} messages of the list again?\n\n" +
            "Messages that a device has already sent again by itself reach their recipients twice.",
            Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
            return;

        int sent = 0;
        string? firstError = null;
        await RunBusyAsync("Sending the messages…", async () =>
        {
            foreach (var row in rows)
            {
                BusyText.Text = $"Sending message {sent + 1} of {rows.Count}…";
                try
                {
                    await _vm.ResendAsync(row);
                    sent++;
                }
                catch (Exception ex)
                {
                    firstError ??= Describe(ex);
                }
            }
        });
        _vm.RefreshUndelivered();

        if (firstError is null)
            ShowInfo($"{sent} message{(sent == 1 ? "" : "s")} sent.");
        else
            ShowError($"{sent} of {rows.Count} messages sent. The others stay in the list.\n\nFirst error: {firstError}");
    }

    private void DeleteUndelivered_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: UndeliveredRow row })
            return;

        var answer = MessageBox.Show(this,
            $"Delete this message without sending it?\n\nSubject: {row.Subject}\nTo: {row.To}",
            Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
            return;

        try
        {
            _vm.DeleteUndelivered(row);
        }
        catch (Exception ex)
        {
            ShowError("The message was not deleted:\n" + ex.Message);
        }
        _vm.RefreshUndelivered();
    }

    // ----- backup and restore ----------------------------------------------------------------------------

    private void Backup_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.IsDirty)
        {
            ShowInfo("Save or discard your changes first: the backup contains the saved settings.");
            return;
        }

        var password = new PasswordDialog(this, "Back up settings",
            "Choose a password for the backup file. It protects the passwords and the other secrets that the file contains; you need it to restore, and it cannot be recovered.",
            SettingsBackup.DescribePasswordProblem, okText: "Continue");
        if (password.ShowDialog() != true)
            return;

        var dialog = new SaveFileDialog
        {
            Title = "Save the backup",
            Filter = $"SMTP Relay backup (*{SettingsBackup.FileExtension})|*{SettingsBackup.FileExtension}",
            FileName = $"SmtpRelay-{Environment.MachineName}-{DateTime.Now:yyyyMMdd}{SettingsBackup.FileExtension}",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            var summary = _vm.CreateBackup(dialog.FileName, password.Password);
            ShowInfo($"Backup saved:\n{dialog.FileName}\n\nIt contains the settings, {summary.Secrets} secret{(summary.Secrets == 1 ? "" : "s")} and {summary.Users} user{(summary.Users == 1 ? "" : "s")}." +
                     (summary.UsesCertificates
                         ? "\n\nYour settings use certificates. They are not in the backup: export them from this computer (with their private key) and import them on the new one."
                         : ""));
        }
        catch (Exception ex)
        {
            ShowError("The backup was not saved:\n" + ex.Message);
        }
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose the backup to restore",
            Filter = $"SMTP Relay backup (*{SettingsBackup.FileExtension})|*{SettingsBackup.FileExtension}|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
            return;

        BackupSummary found;
        try
        {
            found = SettingsBackup.Describe(dialog.FileName);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            return;
        }

        var answer = MessageBox.Show(this,
            $"Restore this backup?\n\nMade on {found.Computer}, {found.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}, version {found.ProductVersion}.\n" +
            $"It contains the settings, {found.Secrets} secret{(found.Secrets == 1 ? "" : "s")} and {found.Users} user{(found.Users == 1 ? "" : "s")}.\n\n" +
            "Every setting, secret and user of this installation is replaced" + (_vm.IsDirty ? ", and your unsaved changes are lost." : "."),
            Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
            return;

        var password = new PasswordDialog(this, "Restore from a backup", "Type the password that was chosen when the backup was made.",
            _ => null, okText: "Restore", confirm: false);
        if (password.ShowDialog() != true)
            return;

        try
        {
            var summary = _vm.RestoreBackup(dialog.FileName, password.Password);
            ShowInfo("The settings were restored." +
                     (summary.UsesCertificates
                         ? "\n\nThey use certificates: import them on this computer, then check the Inbound TLS and Smart host pages."
                         : "") +
                     "\n\nRestart the service to apply them.");
        }
        catch (Exception ex)
        {
            ShowError("Nothing was restored:\n" + ex.Message);
        }
    }

    // ----- users of the relay ----------------------------------------------------------------------------

    private void AddUser_Click(object sender, RoutedEventArgs e) => AddUser();

    private void NewUser_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddUser();
            e.Handled = true;
        }
    }

    private void AddUser()
    {
        var error = _vm.AddUser(NewUserBox.Text, NewUserPasswordBox.Password);
        NewUserError.Text = error ?? "";
        NewUserError.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        if (error is null)
        {
            NewUserBox.Clear();
            NewUserPasswordBox.Clear();
            NewUserBox.Focus();
        }
    }

    private void ChangeUserPassword_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name })
            return;

        var dialog = new PasswordDialog(this, "Change password",
            $"New password of the user '{name}'. Devices that sign in with the old one stop sending until you update them.",
            SmtpRelay.Security.RelayUserStore.DescribePasswordProblem);
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            _vm.ChangeUserPassword(name, dialog.Password);
        }
        catch (Exception ex)
        {
            ShowError("The password was not changed:\n" + ex.Message);
        }
    }

    private void ToggleUser_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RelayUserRow user })
            return;

        try
        {
            _vm.SetUserEnabled(user.Name, !user.Enabled);
        }
        catch (Exception ex)
        {
            ShowError("The user was not changed:\n" + ex.Message);
        }
    }

    private void RemoveUser_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name })
            return;

        var answer = MessageBox.Show(this,
            $"Remove the user '{name}'? Devices that sign in with it will no longer be able to send.",
            Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
            return;

        try
        {
            _vm.RemoveUser(name);
        }
        catch (Exception ex)
        {
            ShowError("The user was not removed:\n" + ex.Message);
        }
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
