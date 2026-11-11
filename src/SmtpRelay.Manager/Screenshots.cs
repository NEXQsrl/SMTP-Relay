#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SmtpRelay.Configuration;
using SmtpRelay.Manager.Services;
using SmtpRelay.Manager.ViewModels;

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
                Security = { AllowedIps = { "127.0.0.1", "192.168.10.0/24", "192.168.20.15", "10.20.0.0/16", "2001:db8:10::/48" } },
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
                }
            };
            ConfigLoader.Save(temp, sample);

            var vm = new MainViewModel(temp, isDemo: true);
            vm.SetDemoServiceState(ServiceState.Running);
            vm.SetStats(1284, 3, new[]
            {
                "2026-11-11 10:42:07.118 +01:00 FAILED client=192.168.10.44 from=scanner@contoso.com to=m.rossi@fabrikam.com size=4821330 subject=Scan 0042 permanent=True reason=5.7.60 Client does not have permissions to send as this sender",
                "2026-11-11 09:15:31.552 +01:00 FAILED client=10.20.4.12 from=erp@contoso.com to=billing@northwind.example size=18244 subject=Invoice 2026/1187 permanent=False reason=4.4.1 Smart host not available",
                "2026-11-11 08:03:12.907 +01:00 FAILED client=192.168.10.61 from=nas@contoso.com to=it@contoso.com size=2210 subject=Backup report permanent=False reason=4.4.1 Smart host not available"
            });
            vm.LogFiles.Add("audit-20261111.log");
            vm.LogFiles.Add("smtp-relay-20261111.log");
            vm.LogFiles.Add("manager-202611.log");
            vm.SelectedLogFile = "audit-20261111.log";
            vm.SetLogText(string.Join(Environment.NewLine, new[]
            {
                "2026-11-11 10:38:02.441 +01:00 DELIVERED client=192.168.10.44 from=scanner@contoso.com to=a.bianchi@contoso.com size=1830221 subject=Scan 0039 durationMs=912",
                "2026-11-11 10:38:40.106 +01:00 DELIVERED client=10.20.4.12 from=erp@contoso.com to=orders@fabrikam.com,cc@contoso.com size=24110 subject=Order confirmation 55012 durationMs=655",
                "2026-11-11 10:39:15.870 +01:00 DELIVERED client=192.168.20.15 from=monitoring@contoso.com to=it@contoso.com size=3402 subject=[OK] Disk space restored on SRV-FS01 durationMs=588",
                "2026-11-11 10:40:01.223 +01:00 DELIVERED client=10.20.4.12 from=erp@contoso.com to=billing@northwind.example size=18244 subject=Invoice 2026/1187 durationMs=701",
                "2026-11-11 10:40:44.019 +01:00 DELIVERED client=192.168.10.61 from=nas@contoso.com to=it@contoso.com size=2210 subject=Backup report durationMs=540",
                "2026-11-11 10:41:20.662 +01:00 DELIVERED client=192.168.10.44 from=scanner@contoso.com to=g.verdi@contoso.com size=920114 subject=Scan 0040 durationMs=834",
                "2026-11-11 10:41:58.340 +01:00 DELIVERED client=192.168.10.44 from=scanner@contoso.com to=g.verdi@contoso.com size=1102938 subject=Scan 0041 durationMs=871",
                "2026-11-11 10:42:07.118 +01:00 FAILED client=192.168.10.44 from=scanner@contoso.com to=m.rossi@fabrikam.com size=4821330 subject=Scan 0042 permanent=True reason=5.7.60 Client does not have permissions to send as this sender",
                "2026-11-11 10:43:11.507 +01:00 DELIVERED client=10.20.4.12 from=erp@contoso.com to=purchasing@fabrikam.com size=31077 subject=Purchase order 88-2210 durationMs=690",
                "2026-11-11 10:44:26.915 +01:00 DELIVERED client=192.168.20.15 from=monitoring@contoso.com to=it@contoso.com size=3518 subject=[WARN] High CPU on SRV-SQL02 durationMs=602",
                "2026-11-11 10:45:02.288 +01:00 DELIVERED client=10.20.7.3 from=helpdesk@contoso.com to=l.neri@contoso.com size=8120 subject=Ticket #20418 updated durationMs=577",
                "2026-11-11 10:46:39.730 +01:00 DELIVERED client=10.20.4.12 from=erp@contoso.com to=billing@northwind.example size=18102 subject=Invoice 2026/1188 durationMs=663"
            }));

            var window = new MainWindow(vm) { Width = 1180, Height = 840, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -4000, Top = 0 };
            window.Show();

            foreach (var page in new[] { "Overview", "Listener", "Access", "SmartHost", "Tls", "Logging", "Logs", "About" })
            {
                if (page == "About")
                {
                    vm.SetRelease(new ReleaseInfo(
                        new Version(2, 1, 0, 0), "v2.1.0",
                        "- Faster delivery: the connection to the smart host is reused between messages\n- New: daily summary of delivered and failed messages\n- Fixes and updated dependencies",
                        AppInfo.ReleasesUrl, AppInfo.ReleasesUrl + "/download/v2.1.0/SmtpRelay-Setup-2.1.0.exe", "SmtpRelay-Setup-2.1.0.exe", 1, null), newer: true);
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
