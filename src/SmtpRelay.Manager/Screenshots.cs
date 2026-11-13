#if DEBUG
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SmtpRelay.Configuration;
using SmtpRelay.Manager.Services;
using SmtpRelay.Manager.ViewModels;
using SmtpRelay.Monitoring;
using SmtpRelay.Relay;

namespace SmtpRelay.Manager;

/// <summary>
/// Development helper (Debug builds only): renders each page of the console with sample data to a PNG file.
/// The pictures in the README are produced this way, so they always show the real interface.
/// </summary>
internal static class Screenshots
{
    public static void Render(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        // Sample settings in a temporary folder: nothing of this computer is read or shown.
        var temp = Directory.CreateTempSubdirectory("smtprelay-shots-").FullName;
        try
        {
            var sample = new RelayOptions
            {
                DataDirectory = @"C:\ProgramData\SmtpRelay",
                Listener = { Port = 25, ServerName = "relay.contoso.local" },
                Security =
                {
                    AllowedIps = { "127.0.0.1", "192.168.10.44", "192.168.10.61", "10.20.4.12", "10.20.0.0/16" },
                    DisabledIps = { "192.168.20.15" },
                    Authentication = { Mode = InboundAuthMode.Optional }
                },
                Tls = { Mode = InboundTlsMode.StartTlsOptional, CertificateThumbprint = "3F2A9C41D07B5E86A1C4F0B92D7E6A5538C1D4E9" },
                Upstream =
                {
                    Mode = UpstreamAuthMode.Microsoft365,
                    Microsoft365 =
                    {
                        TenantId = "contoso.onmicrosoft.com",
                        ClientId = "7c1a2b3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d",
                        SenderUsername = "relay@contoso.com"
                    }
                },
                Snmp = { Enabled = true, AllowedManagers = { "192.168.20.15", "10.20.9.0/24" } }
            };
            ConfigLoader.Save(temp, sample);

            var vm = new MainViewModel(temp, isDemo: true);
            vm.SetDemoServiceState(ServiceState.Running);
            var at = new DateTimeOffset(2026, 11, 11, 10, 46, 0, TimeSpan.FromHours(1));
            vm.SetSenderReport(new SenderReport(new[]
            {
                new SenderActivity(IPAddress.Parse("127.0.0.1"), 12, 0, at.AddDays(-3)),
                new SenderActivity(IPAddress.Parse("192.168.10.44"), 18240, 31, at.AddMinutes(-4)),
                new SenderActivity(IPAddress.Parse("192.168.10.61"), 302, 2, at.AddHours(-2)),
                new SenderActivity(IPAddress.Parse("10.20.4.12"), 9120, 6, at)
            }, new DateOnly(2026, 10, 12)));
            vm.SetHostNames(new Dictionary<string, string>
            {
                ["127.0.0.1"] = "localhost",
                ["192.168.10.44"] = "mfp-reception.contoso.local",
                ["192.168.10.61"] = "nas01.contoso.local",
                ["10.20.4.12"] = "erp-app02.contoso.local",
                ["192.168.20.15"] = ""
            });
            var hours = new[] { 2, 0, 1, 0, 0, 3, 14, 41, 96, 132, 118, 87, 64, 102, 127, 110, 93, 71, 38, 22, 15, 9, 6, 4 };
            vm.SetChart(hours, hours.Select((h, i) => i is 9 or 14 ? 2 : i == 11 ? 1 : 0).ToArray(),
                Enumerable.Range(0, 24).Select(i => at.LocalDateTime.Date.AddHours(11 + i).ToString("HH:mm")).ToArray());
            vm.SetUndelivered(new[]
            {
                new SpooledMessage(@"C:\ProgramData\SmtpRelay\failed\failed-20261111-3f2a9c41d07b5e86.eml", new SpoolRecord
                {
                    TimeUtc = at.UtcDateTime.AddMinutes(-4), Client = "192.168.10.44", User = "-", From = "scanner@contoso.com",
                    Recipients = { "m.rossi@fabrikam.com" }, Subject = "Scan 0042", Permanent = true,
                    Reason = "5.7.60 Client does not have permissions to send as this sender"
                }, 4821330, true),
                new SpooledMessage(@"C:\ProgramData\SmtpRelay\failed\failed-20261111-a1c4f0b92d7e6a55.eml", new SpoolRecord
                {
                    TimeUtc = at.UtcDateTime.AddMinutes(-91), Client = "10.20.4.12", User = "erp@contoso.com", From = "erp@contoso.com",
                    Recipients = { "billing@northwind.example", "accounting@contoso.com" }, Subject = "Invoice 2026/1187", Permanent = false,
                    Reason = "4.4.1 Smart host not available"
                }, 18244, true),
                new SpooledMessage(@"C:\ProgramData\SmtpRelay\failed\failed-20261111-38c1d4e97c1a2b3d.eml", new SpoolRecord
                {
                    TimeUtc = at.UtcDateTime.AddMinutes(-163), Client = "192.168.10.61", User = "nas-backup", From = "nas@contoso.com",
                    Recipients = { "it@contoso.com" }, Subject = "Backup report", Permanent = false,
                    Reason = "4.4.1 Smart host not available"
                }, 2210, true)
            });
            vm.SetUsers(new[] { new RelayUserRow("erp@contoso.com", true), new RelayUserRow("nas-backup", true), new RelayUserRow("old-scanner", false) });
            vm.SetStats(1284, 3, new[]
            {
                "2026-11-11 10:42:07.118 +01:00 FAILED client=192.168.10.44 user=- from=scanner@contoso.com to=m.rossi@fabrikam.com size=4821330 subject=Scan 0042 permanent=True reason=5.7.60 Client does not have permissions to send as this sender",
                "2026-11-11 09:15:31.552 +01:00 FAILED client=10.20.4.12 user=erp@contoso.com from=erp@contoso.com to=billing@northwind.example size=18244 subject=Invoice 2026/1187 permanent=False reason=4.4.1 Smart host not available",
                "2026-11-11 08:03:12.907 +01:00 FAILED client=192.168.10.61 user=nas-backup from=nas@contoso.com to=it@contoso.com size=2210 subject=Backup report permanent=False reason=4.4.1 Smart host not available"
            });
            vm.LogFiles.Add("audit-20261111.log");
            vm.LogFiles.Add("smtp-relay-20261111.log");
            vm.LogFiles.Add("manager-202611.log");
            vm.SelectedLogFile = "audit-20261111.log";
            vm.SetLogText(string.Join(Environment.NewLine, new[]
            {
                "2026-11-11 10:38:02.441 +01:00 DELIVERED client=192.168.10.44 user=- from=scanner@contoso.com to=a.bianchi@contoso.com size=1830221 subject=Scan 0039 durationMs=912",
                "2026-11-11 10:38:40.106 +01:00 DELIVERED client=10.20.4.12 user=erp@contoso.com from=erp@contoso.com to=orders@fabrikam.com,cc@contoso.com size=24110 subject=Order confirmation 55012 durationMs=655",
                "2026-11-11 10:39:15.870 +01:00 DELIVERED client=192.168.20.15 user=- from=monitoring@contoso.com to=it@contoso.com size=3402 subject=[OK] Disk space restored on SRV-FS01 durationMs=588",
                "2026-11-11 10:40:01.223 +01:00 DELIVERED client=10.20.4.12 user=erp@contoso.com from=erp@contoso.com to=billing@northwind.example size=18244 subject=Invoice 2026/1187 durationMs=701",
                "2026-11-11 10:40:44.019 +01:00 DELIVERED client=192.168.10.61 user=nas-backup from=nas@contoso.com to=it@contoso.com size=2210 subject=Backup report durationMs=540",
                "2026-11-11 10:41:20.662 +01:00 DELIVERED client=192.168.10.44 user=- from=scanner@contoso.com to=g.verdi@contoso.com size=920114 subject=Scan 0040 durationMs=834",
                "2026-11-11 10:41:58.340 +01:00 DELIVERED client=192.168.10.44 user=- from=scanner@contoso.com to=g.verdi@contoso.com size=1102938 subject=Scan 0041 durationMs=871",
                "2026-11-11 10:42:07.118 +01:00 FAILED client=192.168.10.44 user=- from=scanner@contoso.com to=m.rossi@fabrikam.com size=4821330 subject=Scan 0042 permanent=True reason=5.7.60 Client does not have permissions to send as this sender",
                "2026-11-11 10:43:11.507 +01:00 DELIVERED client=10.20.4.12 user=erp@contoso.com from=erp@contoso.com to=purchasing@fabrikam.com size=31077 subject=Purchase order 88-2210 durationMs=690",
                "2026-11-11 10:44:26.915 +01:00 DELIVERED client=192.168.20.15 user=- from=monitoring@contoso.com to=it@contoso.com size=3518 subject=[WARN] High CPU on SRV-SQL02 durationMs=602",
                "2026-11-11 10:45:02.288 +01:00 DELIVERED client=10.20.7.3 user=- from=helpdesk@contoso.com to=l.neri@contoso.com size=8120 subject=Ticket #20418 updated durationMs=577",
                "2026-11-11 10:46:39.730 +01:00 DELIVERED client=10.20.4.12 user=erp@contoso.com from=erp@contoso.com to=billing@northwind.example size=18102 subject=Invoice 2026/1188 durationMs=663"
            }));

            var window = new MainWindow(vm) { Width = 1180, Height = 840, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -4000, Top = 0 };
            window.Show();

            foreach (var page in new[] { "Overview", "Listener", "Access", "SmartHost", "Tls", "Logging", "Logs", "Undelivered", "Snmp", "About" })
            {
                if (page == "About")
                {
                    vm.SetRelease(new ReleaseInfo(
                        new Version(2, 5, 0, 0), "v2.5.0",
                        "- Faster delivery: the connection to the smart host is reused between messages\n- New: more figures for your monitoring system\n- Fixes and updated dependencies",
                        AppInfo.ReleasesUrl, AppInfo.ReleasesUrl + "/download/v2.5.0/SmtpRelay-Setup-2.5.0.exe", "SmtpRelay-Setup-2.5.0.exe", 1, null), newer: true);
                }

                vm.Page = page;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
                window.UpdateLayout();
                Save((FrameworkElement)window.Content, Path.Combine(outputDirectory, page.ToLowerInvariant() + ".png"));
            }

            window.Close();
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { }
        }
    }

    private static void Save(FrameworkElement element, string path)
    {
        const double scale = 1.5; // sharper pictures on high-density screens
        var bitmap = new RenderTargetBitmap(
            (int)(element.ActualWidth * scale), (int)(element.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(element);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
#endif
