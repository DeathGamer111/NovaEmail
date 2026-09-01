using System.Net;
using System.Net.Sockets;
using NovaEmail.Intelligence;
using NovaEmail.Safety;
using SecurityException = System.Security.SecurityException;

namespace NovaEmail.Assistant;

/// <summary>
/// Opens assistant sockets only to loopback services or a user-configured public
/// HTTPS origin. DNS results are pinned to the validated socket destination so a
/// public host cannot redirect the connection into a private network.
/// </summary>
public sealed class AssistantEndpointPolicy
{
    private const string LoopbackHostName = "localhost";
    private readonly Func<string, IPAddress[]> _resolver;

    public AssistantEndpointPolicy(Func<string, IPAddress[]>? resolver = null)
    {
        _resolver = resolver ?? Dns.GetHostAddresses;
    }

    public IReadOnlyList<IPEndPoint> ResolveAllowed(string host, int port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
            throw new SecurityException("The assistant endpoint port is invalid.");

        var loopbackRequest = false;
        var publicRequest = false;
        IPAddress[] resolved;
        if (IPAddress.TryParse(host, out var address))
        {
            loopbackRequest = IPAddress.IsLoopback(address);
            if (!loopbackRequest)
                throw new SecurityException("Direct non-loopback assistant IP addresses are blocked.");
            resolved = [address];
        }
        else if (host.Equals(LoopbackHostName, StringComparison.OrdinalIgnoreCase))
        {
            loopbackRequest = true;
            resolved = ResolveBounded(host);
        }
        else if (port == 443)
        {
            publicRequest = true;
            resolved = ResolveBounded(host);
        }
        else
        {
            throw new SecurityException(
                "The assistant endpoint is not loopback and does not use the default HTTPS port.");
        }

        if (resolved.Length is 0 or > 16 ||
            loopbackRequest && !resolved.All(IPAddress.IsLoopback) ||
            publicRequest && resolved.Any(IsProhibitedRemoteAddress))
            throw new SecurityException("The assistant endpoint resolved outside its approved network boundary.");

        return resolved
            .Distinct()
            .Select(candidate => new IPEndPoint(candidate, port))
            .ToArray();
    }

    public async Task<Socket> ConnectAllowedAsync(
        string host,
        int port,
        CancellationToken cancellationToken = default)
    {
        var candidates = ResolveAllowed(host, port);
        SocketException? lastFailure = null;
        foreach (var candidate in candidates)
        {
            var socket = new Socket(candidate.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
            };
            try
            {
                await socket.ConnectAsync(candidate, cancellationToken).ConfigureAwait(false);
                return socket;
            }
            catch (SocketException exception)
            {
                lastFailure = exception;
                socket.Dispose();
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw new IOException("The approved assistant endpoint could not be reached.", lastFailure);
    }

    private IPAddress[] ResolveBounded(string host)
    {
        try
        {
            return _resolver(host);
        }
        catch (SocketException exception)
        {
            throw new IOException("The approved assistant endpoint could not be resolved.", exception);
        }
    }

    private static bool IsProhibitedRemoteAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) ||
            address.Equals(IPAddress.None) || address.Equals(IPAddress.Broadcast) ||
            address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None) ||
            address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal)
            return true;

        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] is 0 or 10 or 127 ||
               bytes[0] == 100 && bytes[1] is >= 64 and <= 127 ||
               bytes[0] == 169 && bytes[1] == 254 ||
               bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
               bytes[0] == 192 && (bytes[1] is 0 or 168 ||
                   bytes[1] == 2 || bytes[1] == 88 && bytes[2] == 99) ||
               bytes[0] == 198 && bytes[1] is 18 or 19 ||
               bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100 ||
               bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113 ||
               bytes[0] >= 224;
    }
}
