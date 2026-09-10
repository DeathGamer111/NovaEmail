using System.Net;
using System.Net.Sockets;
using NovaEmail.Safety;

namespace NovaEmail.Safety.Tests;

public sealed class EndpointPolicyTests
{
    private readonly EndpointPolicy _policy = new();

    [Theory]
    [InlineData("127.0.0.1", 3993)]
    [InlineData("::1", 3143)]
    [InlineData("10.0.0.10", 993)]
    [InlineData("192.168.1.10", 995)]
    [InlineData("8.8.8.8", 465)]
    public void AllowsValidUserConfiguredIpEndpoints(string host, int port)
    {
        _policy.EnsureAllowed(host, port);
    }

    [Theory]
    [InlineData("0.0.0.0", 993)]
    [InlineData("255.255.255.255", 993)]
    [InlineData("::", 993)]
    [InlineData("ff02::1", 993)]
    public void BlocksInvalidNetworkDestinations(string host, int port)
    {
        Assert.Throws<SecurityException>(() => _policy.EnsureAllowed(host, port));
    }

    [Fact]
    public void ResolvedHostnamesMustContainOnlyValidDestinations()
    {
        var policy = new EndpointPolicy(_ =>
        [
            IPAddress.Loopback,
            IPAddress.Any,
        ]);

        Assert.Throws<SecurityException>(() => policy.ResolveAllowed("mail.example.test", 3993));
    }

    [Fact]
    public async Task ConnectAllowedAsyncUsesOneApprovedResolutionSnapshot()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var resolutionCount = 0;
        var policy = new EndpointPolicy(_ =>
        {
            var count = Interlocked.Increment(ref resolutionCount);
            return count == 1
                ? [IPAddress.Loopback]
                : [IPAddress.Parse("203.0.113.10")];
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        using var client = await policy.ConnectAllowedAsync(
            "localhost", ((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token);
        using var accepted = await listener.AcceptSocketAsync(timeout.Token);

        Assert.Equal(1, resolutionCount);
        var remote = Assert.IsType<IPEndPoint>(client.RemoteEndPoint);
        Assert.True(IPAddress.IsLoopback(remote.Address));
    }
}
