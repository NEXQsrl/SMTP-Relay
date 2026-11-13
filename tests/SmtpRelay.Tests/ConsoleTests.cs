using System.Collections.Concurrent;
using SmtpRelay.Configuration;
using SmtpRelay.Manager.Services;
using SmtpRelay.Monitoring;
using SmtpRelay.Security;

namespace SmtpRelay.Tests;

/// <summary>What the console shows: the chart, the disabled entries of the allowlist, one editor at a time.</summary>
public sealed class ConsoleTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("smtprelay-console-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ----- chart -----------------------------------------------------------------------------------------

    [Fact]
    public void TheChartCountsMessagesInThePeriodsOfTheAuditTrail()
    {
        string Line(DateTime local, string what)
            => $"{new DateTimeOffset(local):yyyy-MM-dd HH:mm:ss.fff zzz} {what} client=10.0.0.1 user=- from=a@x.example to=b@y.example size=1 subject=FAILED DELIVERED words";

        var start = new DateTime(2026, 11, 12, 10, 0, 0, DateTimeKind.Local);
        File.WriteAllLines(Path.Combine(_dir, "audit-20261112.log"), new[]
        {
            Line(start.AddMinutes(-1), "DELIVERED"),                    // before the first period
            Line(start, "DELIVERED"),
            Line(start.AddMinutes(59), "DELIVERED"),
            Line(start.AddMinutes(59), "FAILED"),
            Line(start.AddHours(1), "DELIVERED"),
            Line(start.AddHours(2).AddMinutes(30), "FAILED"),
            Line(start.AddHours(3), "DELIVERED"),                        // after the last period
            "2026-11-12 10:30:00.000 +01:00 [INF] something else"
        });
        // The file of the day before can hold the first minutes after midnight; older files are not read.
        File.WriteAllLines(Path.Combine(_dir, "audit-20261111.log"), new[] { Line(start.AddMinutes(5), "DELIVERED") });
        File.WriteAllLines(Path.Combine(_dir, "audit-20261101.log"), new[] { Line(start.AddMinutes(6), "DELIVERED") });

        var (delivered, failed) = AuditTrail.ReadBuckets(_dir, start, TimeSpan.FromHours(1), 3);

        Assert.Equal(new[] { 3, 1, 0 }, delivered);
        Assert.Equal(new[] { 1, 0, 1 }, failed);

        var empty = AuditTrail.ReadBuckets(Path.Combine(_dir, "missing"), start, TimeSpan.FromHours(1), 3);
        Assert.Equal(new[] { 0, 0, 0 }, empty.Delivered);
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(3, 4)]
    [InlineData(5, 8)]
    [InlineData(67, 80)]
    [InlineData(132, 200)]
    [InlineData(1000, 1000)]
    [InlineData(1284, 2000)]
    public void TheScaleOfTheChartEndsOnARoundNumber(int highest, int expected)
    {
        var top = SmtpRelay.Manager.TrendChart.NiceMaximum(highest);
        Assert.Equal(expected, top);
        Assert.True(top >= highest && top % 4 == 0);
    }

    // ----- disabled entries ------------------------------------------------------------------------------

    [Fact]
    public void ADisabledEntryIsKeptInTheSettingsButNotAllowed()
    {
        var options = new RelayOptions
        {
            Upstream = { Mode = UpstreamAuthMode.Anonymous, Host = "mail.example.org" },
            Security = { AllowedIps = { "192.168.10.44" }, DisabledIps = { "192.168.20.15", "10.9.0.0/16" } }
        };
        Assert.Empty(RelayOptionsValidator.Validate(options));

        ConfigLoader.Save(_dir, options);
        var loaded = ConfigLoader.Load(_dir);
        Assert.Equal(new[] { "192.168.20.15", "10.9.0.0/16" }, loaded.Security.DisabledIps);

        // The service builds its list from AllowedIps alone.
        var allowList = new IpAllowList(loaded.Security.AllowedIps, loaded.Security.RejectWhenAllowListEmpty);
        Assert.True(allowList.IsAllowed(System.Net.IPAddress.Parse("192.168.10.44")));
        Assert.False(allowList.IsAllowed(System.Net.IPAddress.Parse("192.168.20.15")));
        Assert.False(allowList.IsAllowed(System.Net.IPAddress.Parse("10.9.1.1")));

        options.Security.DisabledIps.Add("not an address");
        Assert.Contains(RelayOptionsValidator.Validate(options), e => e.StartsWith("Security.DisabledIps"));
    }

    // ----- one editor at a time --------------------------------------------------------------------------

    [Fact]
    public void OnlyOneConsoleEditsAndTheOtherTakesOverWhenItCloses()
    {
        var name = @"Local\smtprelay-test-" + Guid.NewGuid().ToString("N");
        using var first = new ConsoleUser(_dir, name);
        using var second = new ConsoleUser(_dir, name);

        Assert.True(first.Run(l => l.TryAcquire()));
        Assert.True(first.Run(l => l.TryAcquire()));     // asking again changes nothing
        Assert.False(second.Run(l => l.TryAcquire()));   // read-only
        Assert.True(first.Lock.IsOwner);
        Assert.False(second.Lock.IsOwner);

        var holder = second.Lock.ReadHolder();
        Assert.NotNull(holder);
        Assert.Equal(ConsoleLock.CurrentUser, holder.User);

        // The reader asks; the editor sees the request once and declines; the reader is told once.
        Assert.Null(first.Lock.TakeRequest());
        second.Lock.RequestClose();
        var request = first.Lock.TakeRequest();
        Assert.NotNull(request);
        Assert.Equal(ConsoleLock.CurrentUser, request.User);
        Assert.Null(first.Lock.TakeRequest());
        Assert.Null(second.Lock.TakeDeclined());
        first.Lock.Decline();
        Assert.Equal(ConsoleLock.CurrentUser, second.Lock.TakeDeclined());
        Assert.Null(second.Lock.TakeDeclined());
        Assert.False(second.Run(l => l.TryAcquire()));

        // The editor closes: the reader can take over, and is then the one that others see.
        first.Run(l => { l.Release(); return true; });
        Assert.Null(second.Lock.ReadHolder());
        Assert.True(second.Run(l => l.TryAcquire()));
        Assert.NotNull(first.Lock.ReadHolder());
        Assert.False(first.Run(l => l.TryAcquire()));
    }

    [Fact]
    public void AnEditorThatEndsWithoutClosingDoesNotBlockTheOthers()
    {
        var name = @"Local\smtprelay-test-" + Guid.NewGuid().ToString("N");
        using var survivor = new ConsoleUser(_dir, name);

        var crashed = new ConsoleUser(_dir, name);
        Assert.True(crashed.Run(l => l.TryAcquire()));
        Assert.False(survivor.Run(l => l.TryAcquire()));
        crashed.Kill(); // its thread ends while it still holds the lock, as a process that is ended does

        Assert.True(survivor.Run(l => l.TryAcquire()));
    }

    [Fact]
    public void AnOldRequestToCloseIsNotShown()
    {
        var name = @"Local\smtprelay-test-" + Guid.NewGuid().ToString("N");
        using var editor = new ConsoleUser(_dir, name);
        Assert.True(editor.Run(l => l.TryAcquire()));

        File.WriteAllText(Path.Combine(_dir, "console-request.json"),
            System.Text.Json.JsonSerializer.Serialize(new CloseRequest(@"CONTOSO\m.rossi", DateTime.UtcNow.AddHours(-2))));
        Assert.Null(editor.Lock.TakeRequest());
        Assert.False(File.Exists(Path.Combine(_dir, "console-request.json")));
    }

    /// <summary>A console on its own thread: the lock must be taken and released by the same thread, as the UI thread does.</summary>
    private sealed class ConsoleUser : IDisposable
    {
        private readonly BlockingCollection<Action> _work = new();
        private readonly Thread _thread;

        public ConsoleUser(string directory, string mutexName)
        {
            Lock = new ConsoleLock(directory, mutexName);
            _thread = new Thread(() =>
            {
                foreach (var action in _work.GetConsumingEnumerable())
                    action();
            }) { IsBackground = true };
            _thread.Start();
        }

        public ConsoleLock Lock { get; }

        public bool Run(Func<ConsoleLock, bool> action)
        {
            var done = new TaskCompletionSource<bool>();
            _work.Add(() =>
            {
                try { done.SetResult(action(Lock)); }
                catch (Exception ex) { done.SetException(ex); }
            });
            return done.Task.GetAwaiter().GetResult();
        }

        /// <summary>Ends the thread without releasing anything.</summary>
        public void Kill()
        {
            _work.CompleteAdding();
            _thread.Join();
        }

        public void Dispose()
        {
            if (!_work.IsAddingCompleted)
            {
                Run(l => { l.Dispose(); return true; });
                Kill();
            }
        }
    }
}
