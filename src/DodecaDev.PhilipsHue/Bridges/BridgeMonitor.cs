using System.Collections.Concurrent;
using MacroDeck.Sdk.ConfigFlow;
using Serilog;

namespace DodecaDev.PhilipsHue.Bridges;

/// <summary>
/// Keeps <see cref="BridgeRegistry"/> statuses current without making any host call wait on the network:
/// probing runs in the background after <see cref="Start"/> and whenever an action finds a bridge
/// unreachable. An address that changed is written back to the config entry so the next start uses it.
/// </summary>
public sealed class BridgeMonitor(BridgeRegistry registry, BridgeProbe probe, ILogger logger) : IDisposable
{
	private readonly ILogger _logger = logger.ForContext<BridgeMonitor>();
	private readonly ConcurrentDictionary<Guid, byte> _probing = new();
	private readonly Lock _gate = new();
	private CancellationTokenSource _lifetime = new();
	private IIntegrationConfig? _config;

	public void Start(IIntegrationConfig config)
	{
		CancellationToken token;
		lock (_gate)
		{
			_lifetime.Cancel();
			_lifetime.Dispose();
			_lifetime = new CancellationTokenSource();
			_config = config;
			token = _lifetime.Token;
		}

		_ = Task.Run(() => ProbeAllAsync(token), token);
	}

	public void Stop()
	{
		lock (_gate)
		{
			_lifetime.Cancel();
			_config = null;
		}
	}

	public void Dispose()
	{
		lock (_gate)
		{
			_lifetime.Cancel();
			_lifetime.Dispose();
		}
	}

	/// <summary>Re-probes one bridge in the background unless a probe for it is already running.</summary>
	public void RequestProbe(ConfiguredBridge bridge)
	{
		CancellationToken token;
		lock (_gate)
		{
			token = _lifetime.Token;
		}

		if (token.IsCancellationRequested || !_probing.TryAdd(bridge.EntryId, 0))
		{
			return;
		}

		_ = Task.Run(async () =>
		{
			try
			{
				await ProbeNowAsync(bridge, token);
			}
			catch (OperationCanceledException)
			{
			}
			catch (Exception exception)
			{
				_logger.Error(exception, "Probing Hue bridge {BridgeId} failed unexpectedly", bridge.BridgeId);
			}
			finally
			{
				_probing.TryRemove(bridge.EntryId, out _);
			}
		}, token);
	}

	/// <summary>Probes one bridge and applies the result. Used when the user asks for a retry.</summary>
	public async Task<ConfiguredBridge> ProbeNowAsync(ConfiguredBridge bridge, CancellationToken cancellationToken)
	{
		var updated = await probe.ProbeAsync(bridge, cancellationToken);
		await ApplyAsync(bridge, updated, cancellationToken);
		return updated;
	}

	private async Task ProbeAllAsync(CancellationToken cancellationToken)
	{
		try
		{
			var bridges = registry.All;
			var updated = await probe.ProbeAllAsync(bridges, cancellationToken);
			for (var i = 0; i < bridges.Count; i++)
			{
				await ApplyAsync(bridges[i], updated[i], cancellationToken);
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception exception)
		{
			_logger.Error(exception, "Probing Hue bridges failed unexpectedly");
		}
	}

	private async Task ApplyAsync(ConfiguredBridge before, ConfiguredBridge after, CancellationToken cancellationToken)
	{
		registry.Update(after);
		if (after.Host is null || after.Host == before.Host)
		{
			return;
		}

		IIntegrationConfig? config;
		lock (_gate)
		{
			config = _config;
		}

		if (config is null)
		{
			return;
		}

		try
		{
			await config.SetStringAsync(after.EntryId, BridgeConfigKeys.Host, after.Host, cancellationToken);
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			// Losing this write only costs a rediscovery on the next start.
			_logger.Warning(exception, "Could not save the new address of Hue bridge {BridgeId}", after.BridgeId);
		}
	}
}
