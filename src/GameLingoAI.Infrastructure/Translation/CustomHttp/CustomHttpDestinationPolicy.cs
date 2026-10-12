using System.Net;
using System.Net.Sockets;

namespace GameLingoAI.Infrastructure.Translation.CustomHttp;

public interface ICustomHttpDnsResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}

public sealed class SystemCustomHttpDnsResolver : ICustomHttpDnsResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
        Dns.GetHostAddressesAsync(host, cancellationToken);
}

/// <summary>Rejects private, loopback, link-local, and special-use destinations unless local-only is explicit.</summary>
public sealed class CustomHttpDestinationPolicy(ICustomHttpDnsResolver resolver, bool localOnly)
{
    public async Task<IReadOnlyList<IPAddress>> ResolveAllowedAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        cancellationToken.ThrowIfCancellationRequested();
        var addresses = IPAddress.TryParse(endpoint.IdnHost, out var literal)
            ? new[] { literal }
            : await resolver.ResolveAsync(endpoint.IdnHost, cancellationToken).ConfigureAwait(false);
        if (addresses.Length == 0 || addresses.Any(address => !IsAllowed(address, localOnly)))
        {
            throw new CustomHttpDestinationRejectedException();
        }

        return addresses;
    }

    public static bool IsAllowed(IPAddress address, bool localOnly)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (localOnly)
        {
            return IPAddress.IsLoopback(address);
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            var a = bytes[0];
            var b = bytes[1];
            var c = bytes[2];
            return a is >= 1 and <= 223 &&
                a != 10 && a != 127 &&
                !(a == 100 && b is >= 64 and <= 127) &&
                !(a == 169 && b == 254) &&
                !(a == 172 && b is >= 16 and <= 31) &&
                !(a == 192 && b == 168) &&
                !(a == 192 && b == 0 && c is 0 or 2) &&
                !(a == 198 && b is 18 or 19) &&
                !(a == 198 && b == 51 && c == 100) &&
                !(a == 203 && b == 0 && c == 113);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] is >= 0x20 and <= 0x3f &&
                !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8);
        }

        return false;
    }

    public async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var endpoint = new UriBuilder(Uri.UriSchemeHttps, context.DnsEndPoint.Host,
            context.DnsEndPoint.Port).Uri;
        var addresses = await ResolveAllowedAsync(endpoint, cancellationToken).ConfigureAwait(false);
        var address = addresses[0];
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken)
                .ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

public sealed class CustomHttpDestinationRejectedException : Exception;
