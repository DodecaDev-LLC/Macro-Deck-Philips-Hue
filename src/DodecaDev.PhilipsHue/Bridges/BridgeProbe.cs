using DodecaDev.PhilipsHue.Hue;
using Serilog;

namespace DodecaDev.PhilipsHue.Bridges;

/// <summary>
/// Works out whether a configured bridge is usable, and where it is now. A bridge on DHCP can move, and an
/// entry migrated from Macro Deck 2 has no address at all, so an unreachable or missing address falls back
/// to discovery by bridge id.
/// </summary>
public sealed class BridgeProbe(HueClient client, HueDiscovery discovery, ILogger logger)
{
	private readonly ILogger _logger = logger.ForContext<BridgeProbe>();

	/// <summary>Probes every bridge in parallel, calling the discovery service at most once.</summary>
	public async Task<IReadOnlyList<ConfiguredBridge>> ProbeAllAsync(
		IReadOnlyList<ConfiguredBridge> bridges, CancellationToken cancellationToken)
	{
		var discovered = new Lazy<Task<IReadOnlyList<DiscoveredBridge>?>>(() => DiscoverQuietlyAsync(cancellationToken));
		return await Task.WhenAll(bridges.Select(bridge => ProbeAsync(bridge, () => discovered.Value, cancellationToken)));
	}

	public Task<ConfiguredBridge> ProbeAsync(ConfiguredBridge bridge, CancellationToken cancellationToken)
		=> ProbeAsync(bridge, () => DiscoverQuietlyAsync(cancellationToken), cancellationToken);

	private async Task<ConfiguredBridge> ProbeAsync(
		ConfiguredBridge bridge,
		Func<Task<IReadOnlyList<DiscoveredBridge>?>> discover,
		CancellationToken cancellationToken)
	{
		if (bridge.Host is { Length: > 0 } host)
		{
			var status = await VerifyAsync(bridge, host, cancellationToken);
			if (status != BridgeStatus.Unreachable)
			{
				return bridge with { Status = status };
			}
		}

		var found = (await discover())?.FirstOrDefault(candidate => candidate.BridgeId == bridge.BridgeId);
		if (found is null || found.Host == bridge.Host)
		{
			_logger.Warning("Hue bridge {BridgeId} is not reachable and discovery did not find it elsewhere", bridge.BridgeId);
			return bridge with { Status = BridgeStatus.Unreachable };
		}

		_logger.Information("Hue bridge {BridgeId} found at {Host}", bridge.BridgeId, found.Host);
		return bridge with { Host = found.Host, Status = await VerifyAsync(bridge, found.Host, cancellationToken) };
	}

	private async Task<BridgeStatus> VerifyAsync(ConfiguredBridge bridge, string host, CancellationToken cancellationToken)
	{
		try
		{
			await client.VerifyAsync(host, bridge.AppKey, cancellationToken);
			return BridgeStatus.Online;
		}
		catch (HueException exception) when (exception.Reason == HueFailure.Unauthorized)
		{
			_logger.Warning("Hue bridge {BridgeId} rejected the stored app key", bridge.BridgeId);
			return BridgeStatus.Unauthorized;
		}
		catch (HueException exception)
		{
			_logger.Debug(exception, "Hue bridge {BridgeId} did not answer at {Host}", bridge.BridgeId, host);
			return BridgeStatus.Unreachable;
		}
	}

	private async Task<IReadOnlyList<DiscoveredBridge>?> DiscoverQuietlyAsync(CancellationToken cancellationToken)
	{
		try
		{
			return await discovery.DiscoverAsync(cancellationToken);
		}
		catch (HueException exception)
		{
			_logger.Warning(exception, "Hue bridge discovery failed");
			return null;
		}
	}
}
