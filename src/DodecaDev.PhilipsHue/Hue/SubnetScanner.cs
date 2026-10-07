using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using Serilog;

namespace DodecaDev.PhilipsHue.Hue;

/// <summary>Finds bridges by asking every address next to this computer.</summary>
public interface IHueSubnetScanner
{
	Task<IReadOnlyList<DiscoveredBridge>> ScanAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Asks every host in each local IPv4 subnet for <c>/api/config</c>, the fallback the Macro Deck 2 plugin used
/// too. Subnets wider than /24 are only scanned in the /24 around this computer's own address, which keeps
/// the scan to at most 254 short requests per interface.
/// </summary>
public sealed class SubnetScanner(HueClient client, IOptions<HueOptions> options, ILogger logger) : IHueSubnetScanner
{
	private const int MaxParallelRequests = 64;

	private readonly ILogger _logger = logger.ForContext<SubnetScanner>();

	public async Task<IReadOnlyList<DiscoveredBridge>> ScanAsync(CancellationToken cancellationToken)
	{
		var candidates = CandidateAddresses();
		var found = new List<DiscoveredBridge>();
		var gate = new Lock();

		await Parallel.ForEachAsync(
			candidates,
			new ParallelOptions { MaxDegreeOfParallelism = MaxParallelRequests, CancellationToken = cancellationToken },
			async (address, token) =>
			{
				using var perHost = CancellationTokenSource.CreateLinkedTokenSource(token);
				perHost.CancelAfter(options.Value.ScanHostTimeout);
				try
				{
					var info = await client.GetBridgeInfoAsync(address.ToString(), perHost.Token);
					lock (gate)
					{
						found.Add(new DiscoveredBridge(info.BridgeId, address.ToString()));
					}
				}
				catch (OperationCanceledException) when (!token.IsCancellationRequested)
				{
				}
				catch (HueException)
				{
				}
			});

		_logger.Information("Subnet scan of {Count} addresses found {Found} Hue bridge(s)", candidates.Count, found.Count);
		return found;
	}

	internal static List<IPAddress> CandidateAddresses()
	{
		var own = NetworkInterface.GetAllNetworkInterfaces()
			.Where(nic => nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
			.SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
			.Where(unicast => unicast.Address.AddressFamily == AddressFamily.InterNetwork &&
				!IPAddress.IsLoopback(unicast.Address) &&
				!unicast.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
			.ToList();

		var ownAddresses = own.Select(unicast => unicast.Address).ToHashSet();
		return [.. own
			.SelectMany(unicast => HostsAround(unicast.Address, Math.Max(unicast.PrefixLength, 24)))
			.Where(address => !ownAddresses.Contains(address))
			.Distinct()];
	}

	internal static IEnumerable<IPAddress> HostsAround(IPAddress address, int prefixLength)
	{
		if (prefixLength is < 24 or > 30)
		{
			yield break;
		}

		var bytes = address.GetAddressBytes();
		var value = (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
		var mask = uint.MaxValue << (32 - prefixLength);
		var network = value & mask;
		var broadcast = network | ~mask;
		for (var host = network + 1; host < broadcast; host++)
		{
			yield return new IPAddress([(byte)(host >> 24), (byte)(host >> 16), (byte)(host >> 8), (byte)host]);
		}
	}
}
