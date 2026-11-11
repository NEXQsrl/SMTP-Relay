using System.Net;
using SmtpRelay.Security;

namespace SmtpRelay.Tests;

public class IpAllowListTests
{
    [Theory]
    [InlineData("192.168.1.10", "192.168.1.10", true)]
    [InlineData("192.168.1.10", "192.168.1.11", false)]
    [InlineData("192.168.1.0/24", "192.168.1.255", true)]
    [InlineData("192.168.1.0/24", "192.168.2.1", false)]
    [InlineData("10.0.0.0/8", "10.255.1.2", true)]
    [InlineData("10.0.0.0/8", "11.0.0.1", false)]
    [InlineData("192.168.1.77/24", "192.168.1.3", true)] // host bits are ignored
    [InlineData("172.16.0.0/12", "172.31.255.255", true)]
    [InlineData("172.16.0.0/12", "172.32.0.0", false)]
    [InlineData("0.0.0.0/0", "8.8.8.8", true)]
    [InlineData("::1", "::1", true)]
    [InlineData("2001:db8::/32", "2001:db8:1::5", true)]
    [InlineData("2001:db8::/32", "2001:db9::1", false)]
    [InlineData("192.168.1.0/24", "::ffff:192.168.1.5", true)] // IPv4 mapped to IPv6
    [InlineData("::/0", "192.168.1.5", false)] // an IPv6 network never matches a plain IPv4 client
    public void Matches(string entry, string address, bool expected)
    {
        var list = new IpAllowList(new[] { entry }, rejectWhenEmpty: true);
        Assert.Equal(expected, list.IsAllowed(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-ip")]
    [InlineData("192.168.1.0/33")]
    [InlineData("192.168.1.0/-1")]
    [InlineData("192.168.1.0/")]
    [InlineData("192.168.1.0/2 4")]
    [InlineData("::1/129")]
    [InlineData("10")]       // IPAddress.TryParse would read it as 0.0.0.10
    [InlineData("10.1/16")]
    public void RejectsInvalidEntries(string entry)
        => Assert.False(IpAllowList.TryParse(entry, out _));

    [Fact]
    public void EmptyListIsFailClosedByDefault()
    {
        Assert.False(new IpAllowList(Array.Empty<string>(), rejectWhenEmpty: true).IsAllowed(IPAddress.Loopback));
        Assert.True(new IpAllowList(Array.Empty<string>(), rejectWhenEmpty: false).IsAllowed(IPAddress.Loopback));
    }

    [Fact]
    public void NullAddressIsRefused()
        => Assert.False(new IpAllowList(new[] { "0.0.0.0/0" }, rejectWhenEmpty: false).IsAllowed(null));

    [Fact]
    public void DefaultRangeMatchesNothing()
        => Assert.False(default(IpRange).Contains(IPAddress.Loopback));
}
