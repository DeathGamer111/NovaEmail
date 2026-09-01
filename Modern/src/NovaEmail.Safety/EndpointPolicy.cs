using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;

namespace NovaEmail.Safety;

public sealed class EndpointPolicy
{
    private readonly Func<string, IPAddress[]> _resolver;

    public EndpointPolicy(Func<string, IPAddress[]>? resolver = null)
    {
        _resolver = resolver ?? Dns.GetHostAddresses;
    }

    public void EnsureAllowed(string host, int port)
    {
        _ = ResolveAllowed(host, port);
    }

    /// <summary>
    /// Resolves a user-configured mail endpoint exactly once. Callers that open a
    /// socket must use this result rather than resolving the hostname again.
    /// </summary>
    public IReadOnlyList<IPEndPoint> ResolveAllowed(string host, int port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        if (port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
        {
            throw new SecurityException($"Port {port} is outside the valid TCP port range.");
        }

        IPAddress[] resolved;
        if (IPAddress.TryParse(host, out var address))
        {
            resolved = [address];
        }
        else
        {
            try
            {
                resolved = _resolver(host);
            }
            catch (SocketException exception)
            {
                throw new IOException("The mail endpoint could not be resolved.", exception);
            }
        }

        if (resolved.Length is 0 or > 16 || resolved.Any(IsInvalidDestination))
        {
            throw new SecurityException(
                $"Mail endpoint '{host}:{port}' resolved to an invalid network destination.");
        }

        return resolved
            .Distinct()
            .Select(resolvedAddress => new IPEndPoint(resolvedAddress, port))
            .ToArray();
    }

    /// <summary>
    /// Opens a TCP socket exclusively to an address returned by
    /// <see cref="ResolveAllowed"/>. This prevents a hostname from being
    /// re-resolved between policy validation and the network connection.
    /// </summary>
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

        throw new IOException(
            "The configured mail endpoint could not be reached.", lastFailure);
    }

    private static bool IsInvalidDestination(IPAddress address) =>
        address.Equals(IPAddress.Any) ||
        address.Equals(IPAddress.None) ||
        address.Equals(IPAddress.Broadcast) ||
        address.Equals(IPAddress.IPv6Any) ||
        address.Equals(IPAddress.IPv6None) ||
        address.IsIPv6Multicast ||
        address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6);

    public static SslProtocols AllowedTlsProtocols =>
        SslProtocols.Tls12 | SslProtocols.Tls13;
}
