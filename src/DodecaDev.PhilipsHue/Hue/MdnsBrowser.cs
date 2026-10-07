using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;
using Serilog;

namespace DodecaDev.PhilipsHue.Hue;

/// <summary>Finds bridges that announce themselves on the local network.</summary>
public interface IHueMdnsBrowser
{
	Task<IReadOnlyList<MdnsBridge>> BrowseAsync(CancellationToken cancellationToken);
}

/// <summary>A bridge that answered on mDNS. The id is null when its TXT record did not carry one.</summary>
public sealed record MdnsBridge(string Host, string? BridgeId);

/// <summary>
/// Asks for <c>_hue._tcp.local</c> once on every IPv4 interface and collects answers for a short while.
/// Signify recommends mDNS as the primary discovery method; it needs no internet and is not rate-limited.
/// </summary>
/// <remarks>
/// The query sets the unicast-response bit so bridges answer this socket directly. That avoids binding port
/// 5353, which the operating system's own mDNS responder usually holds.
/// </remarks>
public sealed class MdnsBrowser(IOptions<HueOptions> options, ILogger logger) : IHueMdnsBrowser
{
	internal const string ServiceName = "_hue._tcp.local";

	private static readonly IPEndPoint _multicastEndpoint = new(IPAddress.Parse("224.0.0.251"), 5353);

	private readonly ILogger _logger = logger.ForContext<MdnsBrowser>();

	public async Task<IReadOnlyList<MdnsBridge>> BrowseAsync(CancellationToken cancellationToken)
	{
		var addresses = LocalIPv4Addresses();
		var results = await Task.WhenAll(addresses.Select(address => BrowseOnAsync(address, cancellationToken)));
		return [.. results.SelectMany(found => found).DistinctBy(bridge => bridge.Host)];
	}

	private async Task<IReadOnlyList<MdnsBridge>> BrowseOnAsync(IPAddress localAddress, CancellationToken cancellationToken)
	{
		var found = new List<MdnsBridge>();
		try
		{
			using var socket = new UdpClient(new IPEndPoint(localAddress, 0));
			socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, localAddress.GetAddressBytes());
			socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);

			await socket.SendAsync(MdnsMessage.BuildQuery(ServiceName), _multicastEndpoint, cancellationToken);

			using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			window.CancelAfter(options.Value.MdnsTimeout);
			while (true)
			{
				UdpReceiveResult packet;
				try
				{
					packet = await socket.ReceiveAsync(window.Token);
				}
				catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
				{
					break;
				}

				if (MdnsMessage.TryReadHueAnswer(packet.Buffer, packet.RemoteEndPoint.Address, out var bridge))
				{
					found.Add(bridge);
				}
			}
		}
		catch (SocketException exception)
		{
			// One interface that cannot multicast (a VPN adapter, say) must not cost the others.
			_logger.Debug(exception, "mDNS query on {Address} failed", localAddress);
		}

		return found;
	}

	private static List<IPAddress> LocalIPv4Addresses()
		=> [.. NetworkInterface.GetAllNetworkInterfaces()
			.Where(nic => nic.OperationalStatus == OperationalStatus.Up &&
				nic.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
				nic.SupportsMulticast)
			.SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
			.Select(unicast => unicast.Address)
			.Where(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
			.Distinct()];
}

/// <summary>The little of the DNS wire format (RFC 1035, RFC 6762) that browsing for one service needs.</summary>
internal static class MdnsMessage
{
	private const ushort TypeA = 1;
	private const ushort TypePtr = 12;
	private const ushort TypeTxt = 16;
	private const ushort ClassInWithUnicastResponse = 0x8001;

	public static byte[] BuildQuery(string serviceName)
	{
		var buffer = new List<byte>(64);
		buffer.AddRange(new byte[] { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
		WriteName(buffer, serviceName);
		buffer.Add(TypePtr >> 8);
		buffer.Add(TypePtr & 0xFF);
		buffer.Add(ClassInWithUnicastResponse >> 8);
		buffer.Add(ClassInWithUnicastResponse & 0xFF);
		return [.. buffer];
	}

	/// <summary>
	/// Reads a response and, when it answers for the Hue service, the bridge's address and id. The address
	/// comes from an A record when there is one, otherwise from the packet's sender.
	/// </summary>
	public static bool TryReadHueAnswer(byte[] packet, IPAddress sender, out MdnsBridge bridge)
	{
		bridge = new MdnsBridge(sender.ToString(), null);
		try
		{
			if (packet.Length < 12 || (packet[2] & 0x80) == 0)
			{
				return false;
			}

			var questions = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(4));
			var records = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(6)) +
				BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(8)) +
				BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(10));

			var offset = 12;
			for (var i = 0; i < questions; i++)
			{
				ReadName(packet, ref offset);
				offset += 4;
			}

			var isHue = false;
			string? bridgeId = null;
			IPAddress? address = null;
			for (var i = 0; i < records; i++)
			{
				var name = ReadName(packet, ref offset);
				var type = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset));
				var length = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset + 8));
				var data = offset + 10;
				if (data + length > packet.Length)
				{
					return false;
				}

				switch (type)
				{
					case TypePtr when string.Equals(name, MdnsBrowser.ServiceName, StringComparison.OrdinalIgnoreCase):
						isHue = true;
						break;
					case TypeTxt:
						bridgeId ??= ReadBridgeId(packet.AsSpan(data, length));
						break;
					case TypeA when length == 4:
						address ??= new IPAddress(packet.AsSpan(data, 4));
						break;
				}

				offset = data + length;
			}

			if (!isHue)
			{
				return false;
			}

			bridge = new MdnsBridge((address ?? sender).ToString(), bridgeId is null ? null : HueClient.NormalizeBridgeId(bridgeId));
			return true;
		}
		catch (ArgumentOutOfRangeException)
		{
			return false;
		}
		catch (IndexOutOfRangeException)
		{
			return false;
		}
		catch (InvalidDataException)
		{
			return false;
		}
	}

	private static string? ReadBridgeId(ReadOnlySpan<byte> txt)
	{
		var position = 0;
		while (position < txt.Length)
		{
			var length = txt[position++];
			if (position + length > txt.Length)
			{
				return null;
			}

			var entry = Encoding.UTF8.GetString(txt.Slice(position, length));
			if (entry.StartsWith("bridgeid=", StringComparison.OrdinalIgnoreCase) && entry.Length > "bridgeid=".Length)
			{
				return entry["bridgeid=".Length..];
			}

			position += length;
		}

		return null;
	}

	private static void WriteName(List<byte> buffer, string name)
	{
		foreach (var label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
		{
			var bytes = Encoding.UTF8.GetBytes(label);
			buffer.Add((byte)bytes.Length);
			buffer.AddRange(bytes);
		}

		buffer.Add(0);
	}

	/// <summary>Reads a possibly compressed name and moves <paramref name="offset"/> past it.</summary>
	private static string ReadName(byte[] packet, ref int offset)
	{
		var labels = new List<string>();
		var position = offset;
		var jumped = false;
		for (var hops = 0; hops < 32; hops++)
		{
			var length = packet[position];
			if (length == 0)
			{
				if (!jumped)
				{
					offset = position + 1;
				}

				return string.Join('.', labels);
			}

			if ((length & 0xC0) == 0xC0)
			{
				if (!jumped)
				{
					offset = position + 2;
				}

				position = ((length & 0x3F) << 8) | packet[position + 1];
				jumped = true;
				continue;
			}

			labels.Add(Encoding.UTF8.GetString(packet, position + 1, length));
			position += length + 1;
		}

		throw new InvalidDataException("Too many DNS name compression pointers.");
	}
}
