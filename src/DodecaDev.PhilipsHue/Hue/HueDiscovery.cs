using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Serilog;

namespace DodecaDev.PhilipsHue.Hue;

/// <summary>
/// Finds bridges with mDNS, as Signify recommends, and by scanning the local subnet, both at once. Signify's
/// cloud discovery service comes last: it rate-limits callers to roughly one request every few minutes, but it is
/// the only method that finds a bridge on a different subnet.
/// </summary>
public sealed class HueDiscovery(
	IHueMdnsBrowser mdns,
	IHueSubnetScanner scanner,
	HueClient client,
	IHttpClientFactory httpClientFactory,
	IOptions<HueOptions> options,
	ILogger logger)
{
	private readonly ILogger _logger = logger.ForContext<HueDiscovery>();

	public async Task<IReadOnlyList<DiscoveredBridge>> DiscoverAsync(CancellationToken cancellationToken)
	{
		// Both local methods run at once: mDNS waits out its listening window either way.
		var mdnsTask = DiscoverLocallyAsync(cancellationToken);
		var scanTask = ScanQuietlyAsync(cancellationToken);
		var local = (await mdnsTask).Concat(await scanTask).DistinctBy(bridge => bridge.BridgeId).ToList();
		if (local.Count > 0)
		{
			return local;
		}

		return await DiscoverThroughCloudAsync(cancellationToken);
	}

	private async Task<IReadOnlyList<DiscoveredBridge>> DiscoverLocallyAsync(CancellationToken cancellationToken)
	{
		IReadOnlyList<MdnsBridge> answers;
		try
		{
			answers = await mdns.BrowseAsync(cancellationToken);
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			_logger.Warning(exception, "mDNS discovery failed");
			return [];
		}

		var bridges = await Task.WhenAll(answers.Select(answer => ResolveAsync(answer, cancellationToken)));
		return [.. bridges.OfType<DiscoveredBridge>().DistinctBy(bridge => bridge.BridgeId)];
	}

	private async Task<IReadOnlyList<DiscoveredBridge>> ScanQuietlyAsync(CancellationToken cancellationToken)
	{
		try
		{
			return [.. (await scanner.ScanAsync(cancellationToken)).DistinctBy(bridge => bridge.BridgeId)];
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			_logger.Warning(exception, "Subnet scan for Hue bridges failed");
			return [];
		}
	}

	/// <summary>Older firmware announces no bridge id; the bridge's own config always has it.</summary>
	private async Task<DiscoveredBridge?> ResolveAsync(MdnsBridge answer, CancellationToken cancellationToken)
	{
		if (answer.BridgeId is { } bridgeId)
		{
			return new DiscoveredBridge(bridgeId, answer.Host);
		}

		try
		{
			var info = await client.GetBridgeInfoAsync(answer.Host, cancellationToken);
			return new DiscoveredBridge(info.BridgeId, answer.Host);
		}
		catch (HueException exception)
		{
			_logger.Debug(exception, "mDNS answer from {Host} is not a reachable Hue bridge", answer.Host);
			return null;
		}
	}

	private async Task<IReadOnlyList<DiscoveredBridge>> DiscoverThroughCloudAsync(CancellationToken cancellationToken)
	{
		var httpClient = httpClientFactory.CreateClient(HueClient.HttpClientName);
		try
		{
			using var response = await httpClient.GetAsync(options.Value.DiscoveryUri, cancellationToken);
			if (!response.IsSuccessStatusCode)
			{
				_logger.Information("The Hue discovery service answered {Status}", (int)response.StatusCode);
				throw HueException.DiscoveryFailed();
			}

			await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
			var entries = await JsonSerializer.DeserializeAsync<List<DiscoveryEntry>>(stream, cancellationToken: cancellationToken);
			return [.. (entries ?? [])
				.Where(entry => !string.IsNullOrWhiteSpace(entry.Id) && !string.IsNullOrWhiteSpace(entry.InternalIpAddress))
				.Select(entry => new DiscoveredBridge(HueClient.NormalizeBridgeId(entry.Id!), entry.InternalIpAddress!))];
		}
		catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
		{
			throw HueException.DiscoveryFailed(exception);
		}
		catch (HttpRequestException exception)
		{
			throw HueException.DiscoveryFailed(exception);
		}
		catch (JsonException exception)
		{
			throw HueException.DiscoveryFailed(exception);
		}
	}

	private sealed record DiscoveryEntry(
		[property: JsonPropertyName("id")] string? Id,
		[property: JsonPropertyName("internalipaddress")] string? InternalIpAddress);
}
