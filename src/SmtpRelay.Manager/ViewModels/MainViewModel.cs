using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using SmtpRelay.Configuration;
using SmtpRelay.Manager.Services;
using SmtpRelay.Relay;
using SmtpRelay.Security;

namespace SmtpRelay.Manager.ViewModels;

/// <summary>A certificate of LocalMachine\My that can be used for inbound TLS.</summary>
public sealed record CertificateChoice(string Thumbprint, string Display);

/// <summary>State of the management console.</summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly string _configDirectory;
    private readonly ServiceManager _service = new();
    private readonly UpdateService _updates = new();
    private SecretStore? _secrets;
    private string _savedSnapshot = "";

    public MainViewModel(string configDirectory, bool isDemo = false)
    {
        _configDirectory = configDirectory;
        IsDemo = isDemo;
        Options = new RelayOptions();
        Reload();
    }

    // ----- configuration ---------------------------------------------------------------------------------

    public RelayOptions Options { get; private set; }
    public string ConfigPath => IsDemo
        ? @"C:\Program Files\NEXQ\SMTP Relay\appsettings.json"
        : Path.Combine(_configDirectory, ConfigLoader.FileName);
    /// <summary>Sample data for the documentation pictures: nothing of this computer is read or written.</summary>
    public bool IsDemo { get; }

    public ObservableCollection<string> AllowedIps { get; } = new();
    public ObservableCollection<string> Errors { get; } = new();
    public ObservableCollection<string> Warnings { get; } = new();
    public ObservableCollection<CertificateChoice> Certificates { get; } = new();

    public static UpstreamSecureMode[] SecureModes { get; } = Enum.GetValues<UpstreamSecureMode>();
    public static InboundTlsMode[] TlsModes { get; } = Enum.GetValues<InboundTlsMode>();
    public static string[] LogLevels { get; } = { "Verbose", "Debug", "Information", "Warning", "Error", "Fatal" };
    public static string[] TlsProtocols { get; } = { "Tls12", "Tls13" };

    public void Reload()
    {
        Options = ConfigLoader.Load(_configDirectory);
        if (!IsDemo)
            ManagerLog.Initialize(Options.LogDirectory);

        AllowedIps.Clear();
        foreach (var entry in Options.Security.AllowedIps)
            AllowedIps.Add(entry);

        _secrets = null;
        SecretStoreError = null;
        if (!IsDemo)
        {
            try { _secrets = SecretStore.Open(Options); }
            catch (Exception ex) { SecretStoreError = ex.Message; }
        }

        _savedSnapshot = Snapshot();
        LoadCertificates();
        OnPropertyChanged(string.Empty); // every binding reads the new options
        Revalidate();
    }

    private string Snapshot()
    {
        Options.Security.AllowedIps = AllowedIps.ToList();
        return JsonSerializer.Serialize(Options);
    }

    public bool IsDirty => Snapshot() != _savedSnapshot;

    /// <summary>Recomputes errors, warnings and the "unsaved changes" state. Called on a timer while editing.</summary>
    public void Revalidate()
    {
        Options.Security.AllowedIps = AllowedIps.ToList();
        Replace(Errors, RelayOptionsValidator.Validate(Options));

        var warnings = RelayOptionsValidator.Warnings(Options).ToList();
        if (_secrets is not null && !IsDemo)
        {
            if (Options.Upstream.Mode == UpstreamAuthMode.Microsoft365 && !_secrets.Contains(Options.Upstream.Microsoft365.ClientSecretName))
                warnings.Insert(0, "The Microsoft 365 client secret is not stored yet (Smart host page).");
            if (Options.Upstream.Mode == UpstreamAuthMode.Basic && !_secrets.Contains(Options.Upstream.Basic.PasswordSecret))
                warnings.Insert(0, "The smart host password is not stored yet (Smart host page).");
        }
        else if (SecretStoreError is not null)
        {
            warnings.Insert(0, "The secret store cannot be opened: " + SecretStoreError);
        }
        Replace(Warnings, warnings);

        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(HasErrors));
        OnPropertyChanged(nameof(HasWarnings));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(FooterText));
        OnPropertyChanged(nameof(FooterKind));
        OnPropertyChanged(nameof(HealthText));
        OnPropertyChanged(nameof(HealthKind));
        OnPropertyChanged(nameof(ListenerSummary));
        OnPropertyChanged(nameof(UpstreamSummary));
        OnPropertyChanged(nameof(AccessSummary));
        OnPropertyChanged(nameof(ClientSecretStatus));
        OnPropertyChanged(nameof(PasswordStatus));
        OnPropertyChanged(nameof(PfxPasswordStatus));
    }

    private static void Replace(ObservableCollection<string> target, IEnumerable<string> items)
    {
        var list = items.ToList();
        if (target.SequenceEqual(list))
            return;
        target.Clear();
        foreach (var item in list)
            target.Add(item);
    }

    public bool HasErrors => Errors.Count > 0;
    public bool HasWarnings => Warnings.Count > 0;
    public bool CanSave => IsDirty && !HasErrors;

    public string FooterKind => HasErrors ? "Error" : IsDirty ? "Dirty" : "Ok";

    public string FooterText => HasErrors
        ? $"{Errors.Count} problem{(Errors.Count == 1 ? "" : "s")} to fix before saving: {Errors[0]}"
        : IsDirty
            ? "You have unsaved changes. The service uses them after it is restarted."
            : "All changes saved.";

    public string HealthKind => HasErrors ? "Error" : HasWarnings ? "Dirty" : "Ok";

    public string HealthText => HasErrors
        ? "The configuration has errors"
        : HasWarnings ? "The configuration is valid, with warnings" : "The configuration is valid";

    public string ListenerSummary => $"{Options.Listener.IpAddress}:{Options.Listener.Port}";

    public string UpstreamSummary => Options.Upstream.Mode switch
    {
        UpstreamAuthMode.Microsoft365 => $"Microsoft 365 (OAuth2) · {Options.Upstream.Host}:{Options.Upstream.Port}",
        UpstreamAuthMode.Basic => $"User name and password · {Options.Upstream.Host}:{Options.Upstream.Port}",
        _ => $"No authentication · {Options.Upstream.Host}:{Options.Upstream.Port}"
    };

    public string AccessSummary => AllowedIps.Count switch
    {
        0 => Options.Security.RejectWhenAllowListEmpty ? "Nobody (the allowlist is empty)" : "Anyone (open relay)",
        1 => "1 address or network",
        _ => $"{AllowedIps.Count} addresses or networks"
    };

    /// <summary>Saves the settings. Returns false when they are not valid.</summary>
    public bool Save()
    {
        Revalidate();
        if (HasErrors)
            return false;

        ConfigLoader.Save(_configDirectory, Options);
        _savedSnapshot = Snapshot();
        ManagerLog.Initialize(Options.LogDirectory);
        ManagerLog.Write($"Settings saved to {ConfigPath}.");
        Revalidate();
        return true;
    }

    // ----- upstream mode ---------------------------------------------------------------------------------

    public bool IsMicrosoft365
    {
        get => Options.Upstream.Mode == UpstreamAuthMode.Microsoft365;
        set { if (value) SetMode(UpstreamAuthMode.Microsoft365); }
    }

    public bool IsBasic
    {
        get => Options.Upstream.Mode == UpstreamAuthMode.Basic;
        set { if (value) SetMode(UpstreamAuthMode.Basic); }
    }

    public bool IsAnonymous
    {
        get => Options.Upstream.Mode == UpstreamAuthMode.Anonymous;
        set { if (value) SetMode(UpstreamAuthMode.Anonymous); }
    }

    private void SetMode(UpstreamAuthMode mode)
    {
        if (Options.Upstream.Mode == mode)
            return;

        Options.Upstream.Mode = mode;
        // Sensible defaults when switching to Microsoft 365; other modes keep what the administrator typed.
        if (mode == UpstreamAuthMode.Microsoft365)
        {
            Options.Upstream.Host = "smtp.office365.com";
            Options.Upstream.Port = 587;
            Options.Upstream.Security = UpstreamSecureMode.StartTls;
            OnPropertyChanged(nameof(Options));
        }
        OnPropertyChanged(nameof(IsMicrosoft365));
        OnPropertyChanged(nameof(IsBasic));
        OnPropertyChanged(nameof(IsAnonymous));
        Revalidate();
    }

    public double MaxMessageSizeMb
    {
        get => Math.Round(Options.Listener.MaxMessageSizeBytes / 1048576.0, 1);
        set => Options.Listener.MaxMessageSizeBytes = (long)(value * 1048576);
    }

    public double LogFileSizeMb
    {
        get => Math.Round(Options.Logging.FileSizeLimitBytes / 1048576.0, 1);
        set => Options.Logging.FileSizeLimitBytes = (long)(value * 1048576);
    }

    /// <summary>Days the logs are kept; empty means no time limit.</summary>
    public string LogRetentionDays
    {
        get => Options.Logging.RetainedFileTimeLimitDays?.ToString() ?? "";
        set => Options.Logging.RetainedFileTimeLimitDays = int.TryParse(value, out var days) ? days : null;
    }

    public bool TlsEnabled => Options.Tls.Mode != InboundTlsMode.None;

    public InboundTlsMode TlsMode
    {
        get => Options.Tls.Mode;
        set
        {
            Options.Tls.Mode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TlsEnabled));
        }
    }

    // ----- allowlist -------------------------------------------------------------------------------------

    /// <summary>Adds an entry to the allowlist. Returns an error message, or null when it was added.</summary>
    public string? AddAllowedIp(string? entry)
    {
        entry = entry?.Trim() ?? "";
        if (entry.Length == 0)
            return "Type an IP address or a network, for example 192.168.10.0/24.";
        if (!IpAllowList.TryParse(entry, out _))
            return $"'{entry}' is not an IP address or a CIDR network.";
        if (AllowedIps.Contains(entry, StringComparer.OrdinalIgnoreCase))
            return $"'{entry}' is already in the list.";

        AllowedIps.Add(entry);
        Revalidate();
        return null;
    }

    public void RemoveAllowedIp(string entry)
    {
        AllowedIps.Remove(entry);
        Revalidate();
    }

    // ----- secrets ---------------------------------------------------------------------------------------

    public string? SecretStoreError { get; private set; }

    public string ClientSecretStatus => SecretStatus(Options.Upstream.Microsoft365.ClientSecretName);
    public string PasswordStatus => SecretStatus(Options.Upstream.Basic.PasswordSecret);
    public string PfxPasswordStatus => SecretStatus(Options.Tls.CertificatePasswordSecret);

    private string SecretStatus(string? name)
    {
        if (IsDemo)
            return "Stored (encrypted)";
        if (_secrets is null || string.IsNullOrWhiteSpace(name))
            return "Not stored";
        return _secrets.Contains(name) ? "Stored (encrypted)" : "Not stored";
    }

    public void SaveSecret(string name, string value)
    {
        var store = _secrets ?? throw new InvalidOperationException(SecretStoreError ?? "The secret store is not available.");
        store.Set(name, value);
        ManagerLog.Write($"Secret '{name}' stored.");
        Revalidate();
    }

    public void RemoveSecret(string name)
    {
        if (_secrets?.Remove(name) == true)
            ManagerLog.Write($"Secret '{name}' removed.");
        Revalidate();
    }

    // ----- inbound TLS certificates ----------------------------------------------------------------------

    private void LoadCertificates()
    {
        Certificates.Clear();
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);
            foreach (var certificate in store.Certificates.OrderByDescending(c => c.NotAfter))
            {
                if (certificate.HasPrivateKey)
                {
                    var name = certificate.GetNameInfo(X509NameType.SimpleName, false);
                    Certificates.Add(new CertificateChoice(
                        certificate.Thumbprint,
                        $"{name}  ·  expires {certificate.NotAfter:yyyy-MM-dd}  ·  {certificate.Thumbprint[..8]}…"));
                }
                certificate.Dispose();
            }
        }
        catch (Exception)
        {
            // The list is a convenience: the thumbprint can still be typed.
        }
    }

    // ----- service ---------------------------------------------------------------------------------------

    private ServiceState _serviceState = ServiceState.NotInstalled;

    public ServiceState ServiceState
    {
        get => _serviceState;
        private set
        {
            if (_serviceState == value)
                return;
            _serviceState = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ServiceStateText));
            OnPropertyChanged(nameof(ServiceStateKind));
            OnPropertyChanged(nameof(ServiceDetail));
            OnPropertyChanged(nameof(IsServiceInstalled));
            OnPropertyChanged(nameof(IsServiceRunning));
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(CanStop));
        }
    }

    public string ServiceStateText => ServiceState switch
    {
        ServiceState.Running => "Running",
        ServiceState.Stopped => "Stopped",
        ServiceState.Starting => "Starting…",
        ServiceState.Stopping => "Stopping…",
        ServiceState.NotInstalled => "Not installed",
        _ => "Unknown"
    };

    public string ServiceStateKind => ServiceState switch
    {
        ServiceState.Running => "Ok",
        ServiceState.Starting or ServiceState.Stopping => "Dirty",
        ServiceState.Stopped => "Error",
        _ => "Off"
    };

    public string ServiceDetail => ServiceState switch
    {
        ServiceState.Running => $"Accepting mail on {ListenerSummary}",
        ServiceState.Stopped => "The relay is not accepting mail",
        ServiceState.NotInstalled => "The Windows service is not installed on this computer",
        _ => "Please wait"
    };

    public bool IsServiceInstalled => ServiceState != ServiceState.NotInstalled;
    public bool IsServiceRunning => ServiceState == ServiceState.Running;
    public bool CanStart => ServiceState == ServiceState.Stopped;
    public bool CanStop => ServiceState == ServiceState.Running;

    public void RefreshService()
    {
        if (!IsDemo)
            ServiceState = _service.GetState(Options.ServiceName);
    }

    public void SetDemoServiceState(ServiceState state) => ServiceState = state;

    public Task StartServiceAsync() => RunServiceActionAsync("start", () => _service.Start(Options.ServiceName));
    public Task StopServiceAsync() => RunServiceActionAsync("stop", () => _service.Stop(Options.ServiceName));
    public Task RestartServiceAsync() => RunServiceActionAsync("restart", () => _service.Restart(Options.ServiceName));

    private async Task RunServiceActionAsync(string action, Action work)
    {
        var name = Options.ServiceName;
        try
        {
            await Task.Run(work);
            ManagerLog.Write($"Service '{name}': {action} succeeded.");
        }
        catch (Exception ex)
        {
            ManagerLog.Write($"Service '{name}': {action} failed: {ex.Message}");
            throw;
        }
        finally
        {
            RefreshService();
        }
    }

    public async Task<(int ExitCode, string Output)> InstallServiceAsync()
    {
        var result = await Task.Run(() => ServiceManager.RunCli("install"));
        ManagerLog.Write($"Service install: exit code {result.ExitCode}.");
        RefreshService();
        return result;
    }

    // ----- today's activity ------------------------------------------------------------------------------

    public int DeliveredToday { get; private set; }
    public int FailedToday { get; private set; }
    public ObservableCollection<string> RecentFailures { get; } = new();

    public void RefreshStats()
    {
        if (IsDemo)
            return;
        var (delivered, failed, failures) = LogReader.ReadTodayStats(Options.LogDirectory);
        SetStats(delivered, failed, failures);
    }

    public void SetStats(int delivered, int failed, IEnumerable<string> failures)
    {
        DeliveredToday = delivered;
        FailedToday = failed;
        Replace(RecentFailures, failures);
        OnPropertyChanged(nameof(DeliveredToday));
        OnPropertyChanged(nameof(FailedToday));
        OnPropertyChanged(nameof(HasRecentFailures));
    }

    public bool HasRecentFailures => RecentFailures.Count > 0;

    // ----- connection tests ------------------------------------------------------------------------------

    private UpstreamSender CreateSender()
    {
        var secrets = _secrets ?? throw new InvalidOperationException(SecretStoreError ?? "The secret store is not available.");
        var tokens = new Microsoft365TokenProvider(Options.Upstream.Microsoft365, secrets, NullLogger<Microsoft365TokenProvider>.Instance);
        // One attempt only: a test must answer quickly instead of retrying in the background.
        var upstream = JsonSerializer.Deserialize<UpstreamOptions>(JsonSerializer.Serialize(Options.Upstream))!;
        upstream.MaxAttempts = 1;
        upstream.TimeoutSeconds = Math.Min(upstream.TimeoutSeconds, 30);
        return new UpstreamSender(upstream, secrets, tokens, NullLogger<UpstreamSender>.Instance);
    }

    /// <summary>Connects and authenticates to the smart host with the settings on screen (saved or not).</summary>
    public async Task TestConnectionAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await CreateSender().VerifyConnectionAsync(cts.Token);
        ManagerLog.Write($"Connection test to {Options.Upstream.Host}:{Options.Upstream.Port} succeeded.");
    }

    /// <summary>Sends a short test message through the smart host with the settings on screen.</summary>
    public async Task SendTestMessageAsync(string from, string to)
    {
        using var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(from));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = $"{AppInfo.ProductName}: test message";
        message.Body = new TextPart("plain")
        {
            Text = $"This is a test message sent from the management console of {AppInfo.ProductName} {AppInfo.Version}\r\n" +
                   $"on {Environment.MachineName} at {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}.\r\n\r\n" +
                   "If you are reading it, the smart host settings work.\r\n"
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await CreateSender().SendAsync(message, from, new[] { to }, cts.Token);
        ManagerLog.Write($"Test message sent to {to} through {Options.Upstream.Host}.");
    }

    public string DefaultTestSender => Options.Upstream.Mode switch
    {
        _ when !string.IsNullOrWhiteSpace(Options.Upstream.OverrideFromAddress) => Options.Upstream.OverrideFromAddress!,
        UpstreamAuthMode.Microsoft365 => Options.Upstream.Microsoft365.SenderUsername,
        UpstreamAuthMode.Basic when Options.Upstream.Basic.Username.Contains('@') => Options.Upstream.Basic.Username,
        _ => ""
    };

    // ----- logs ------------------------------------------------------------------------------------------

    public ObservableCollection<string> LogFiles { get; } = new();

    private string? _selectedLogFile;
    public string? SelectedLogFile
    {
        get => _selectedLogFile;
        set
        {
            if (_selectedLogFile == value)
                return;
            _selectedLogFile = value;
            OnPropertyChanged();
            RefreshLogText();
        }
    }

    private string _logFilter = "";
    public string LogFilter
    {
        get => _logFilter;
        set
        {
            _logFilter = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsLogFilterEmpty));
            RefreshLogText();
        }
    }

    public bool IsLogFilterEmpty => _logFilter.Length == 0;
    public string LogText { get; private set; } = "";
    public bool AutoRefreshLogs { get; set; } = true;

    public void RefreshLogFiles()
    {
        if (IsDemo)
            return;

        var files = LogReader.ListFiles(Options.LogDirectory);
        if (!LogFiles.SequenceEqual(files))
        {
            var selected = _selectedLogFile;
            LogFiles.Clear();
            foreach (var file in files)
                LogFiles.Add(file);
            _selectedLogFile = selected is not null && files.Contains(selected) ? selected : files.FirstOrDefault();
            OnPropertyChanged(nameof(SelectedLogFile));
        }
        RefreshLogText();
    }

    public void RefreshLogText()
    {
        if (IsDemo)
            return;

        var text = _selectedLogFile is null
            ? "No log file yet. Logs are written when the service runs."
            : string.Join(Environment.NewLine, LogReader.Tail(Path.Combine(Options.LogDirectory, _selectedLogFile), 1000, _logFilter));
        SetLogText(text);
    }

    public void SetLogText(string text)
    {
        if (LogText == text)
            return;
        LogText = text;
        OnPropertyChanged(nameof(LogText));
    }

    // ----- updates ---------------------------------------------------------------------------------------

    public ReleaseInfo? LatestRelease { get; private set; }
    public bool UpdateAvailable { get; private set; }
    public bool IsCheckingUpdates { get; private set; }
    public bool IsInstallingUpdate { get; private set; }
    public double UpdateProgress { get; private set; }
    public string UpdateStatus { get; private set; } = "Not checked yet.";
    public string UpdateTitle => LatestRelease is null ? "" : $"Version {LatestRelease.Tag.TrimStart('v', 'V')} is available";
    public string UpdateNotes => LatestRelease?.Notes ?? "";
    public bool CanInstallUpdate => UpdateAvailable && !IsInstallingUpdate && LatestRelease?.InstallerUrl is not null;

    public bool CheckUpdatesOnStartup
    {
        get => Options.Updates.CheckOnStartup;
        set { Options.Updates.CheckOnStartup = value; OnPropertyChanged(); }
    }

    public async Task CheckForUpdatesAsync()
    {
        if (IsCheckingUpdates)
            return;

        IsCheckingUpdates = true;
        UpdateStatus = "Contacting GitHub…";
        NotifyUpdate();
        try
        {
            var release = await _updates.GetLatestAsync(CancellationToken.None);
            if (release is null)
            {
                LatestRelease = null;
                UpdateAvailable = false;
                UpdateStatus = "No release is published yet.";
            }
            else
            {
                SetRelease(release, UpdateService.IsNewer(release));
            }
        }
        catch (Exception ex)
        {
            UpdateAvailable = false;
            UpdateStatus = "Could not check for updates: " + ex.Message;
        }
        finally
        {
            IsCheckingUpdates = false;
            NotifyUpdate();
        }
    }

    public void SetRelease(ReleaseInfo release, bool newer)
    {
        LatestRelease = release;
        UpdateAvailable = newer;
        UpdateStatus = newer
            ? $"Version {release.Tag.TrimStart('v', 'V')} is available (you have {AppInfo.Version})."
            : $"You have the latest version ({AppInfo.Version}). Checked {DateTime.Now:HH:mm}.";
        NotifyUpdate();
    }

    /// <summary>Downloads, verifies and starts the setup of the latest release. Returns the path of the setup.</summary>
    public async Task<string> DownloadUpdateAsync()
    {
        var release = LatestRelease ?? throw new InvalidOperationException("No release to install.");
        IsInstallingUpdate = true;
        UpdateProgress = 0;
        UpdateStatus = "Downloading the update…";
        NotifyUpdate();
        try
        {
            var progress = new Progress<double>(value =>
            {
                UpdateProgress = value;
                OnPropertyChanged(nameof(UpdateProgress));
            });
            var path = await _updates.DownloadAndVerifyAsync(release, Options.UpdatesDirectory, progress, CancellationToken.None);
            UpdateStatus = "Update verified. Starting the setup…";
            ManagerLog.Write($"Update {release.Tag} downloaded and verified: {path}");
            return path;
        }
        catch (Exception ex)
        {
            UpdateStatus = "The update was not installed: " + ex.Message;
            ManagerLog.Write($"Update {release.Tag} refused: {ex.Message}");
            throw;
        }
        finally
        {
            IsInstallingUpdate = false;
            NotifyUpdate();
        }
    }

    private void NotifyUpdate()
    {
        OnPropertyChanged(nameof(LatestRelease));
        OnPropertyChanged(nameof(UpdateAvailable));
        OnPropertyChanged(nameof(IsCheckingUpdates));
        OnPropertyChanged(nameof(IsInstallingUpdate));
        OnPropertyChanged(nameof(UpdateProgress));
        OnPropertyChanged(nameof(UpdateStatus));
        OnPropertyChanged(nameof(UpdateTitle));
        OnPropertyChanged(nameof(UpdateNotes));
        OnPropertyChanged(nameof(CanInstallUpdate));
    }

    // ----- navigation ------------------------------------------------------------------------------------

    private string _page = "Overview";
    public string Page
    {
        get => _page;
        set
        {
            if (_page == value)
                return;
            _page = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PageTitle));
            OnPropertyChanged(nameof(PageSubtitle));
        }
    }

    public string PageTitle => Page switch
    {
        "Listener" => "Listener",
        "Access" => "Allowed senders",
        "SmartHost" => "Smart host",
        "Tls" => "Inbound TLS",
        "Logging" => "Logging & retention",
        "Logs" => "Logs",
        "About" => "About & updates",
        _ => "Overview"
    };

    public string PageSubtitle => Page switch
    {
        "Listener" => "Where the relay listens and the limits it applies to incoming mail.",
        "Access" => "Only these IP addresses and networks may send mail through the relay.",
        "SmartHost" => "The server the mail is forwarded to, and how the relay signs in.",
        "Tls" => "Encryption between your devices and the relay (STARTTLS).",
        "Logging" => "What is recorded, where, and for how long.",
        "Logs" => "Application log, delivery audit trail and console activity.",
        "About" => "Version, updates and license.",
        _ => "State of the relay on this computer."
    };

    public string VersionText => "Version " + AppInfo.Version;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
