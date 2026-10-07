using DodecaDev.PhilipsHue.Actions;
using DodecaDev.PhilipsHue.Bridges;
using DodecaDev.PhilipsHue.Hue;
using DodecaDev.PhilipsHue.Migration;
using DodecaDev.PhilipsHue.Setup;
using MacroDeck.Localization;
using MacroDeck.Plugin.Hosting;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.ConfigFlow;
using MacroDeck.Sdk.Issues;
using MacroDeck.Sdk.Migration;
using Microsoft.Extensions.Options;
using Serilog;

namespace DodecaDev.PhilipsHue;

/// <summary>
/// One config entry per paired bridge. The entries are read into <see cref="BridgeRegistry"/> on every
/// initialization, and <see cref="BridgeMonitor"/> works out in the background which bridges answer.
/// </summary>
public sealed class PhilipsHueIntegration : IPluginIntegration, IConfigFlowProvider, IIntegrationIssueProvider, IMigrationProvider
{
	private const string UnauthorizedIssuePrefix = "unauthorized-";
	private const string UnreachableIssuePrefix = "unreachable-";

	private readonly HueClient _client;
	private readonly HueDiscovery _discovery;
	private readonly BridgeRegistry _registry;
	private readonly BridgeMonitor _monitor;
	private readonly IOptions<HueOptions> _options;
	private readonly ILogger _logger;

	public PhilipsHueIntegration(
		HueClient client,
		HueDiscovery discovery,
		BridgeRegistry registry,
		BridgeMonitor monitor,
		HueActionSupport support,
		IOptions<HueOptions> options,
		PluginMetadata metadata,
		ILogger logger)
	{
		_client = client;
		_discovery = discovery;
		_registry = registry;
		_monitor = monitor;
		_options = options;
		_logger = logger.ForContext<PhilipsHueIntegration>();
		Actions = [new SetSceneAction(support), new UpdateLightAction(support), new AdjustLightAction(support)];
		Migrations = [new MacroDeck2Migration(metadata.Id)];
	}

	public IReadOnlyList<IActionDefinition> Actions { get; }

	public IReadOnlyList<IIntegrationMigration> Migrations { get; }

	public bool AllowsMultipleConfigurations => true;

	public bool RequiresConfiguration => true;

	public async Task InitializeAsync(IIntegrationContext context)
	{
		var bridges = new List<ConfiguredBridge>();
		foreach (var entry in await context.Config.GetEntriesAsync())
		{
			var bridgeId = await context.Config.GetStringAsync(entry.Id, BridgeConfigKeys.BridgeId);
			var appKey = await context.Config.GetSecretAsync(entry.Id, BridgeConfigKeys.AppKey);
			if (string.IsNullOrWhiteSpace(bridgeId) || string.IsNullOrWhiteSpace(appKey))
			{
				_logger.Warning("Config entry {Entry} has no bridge id or app key and is ignored", entry.Title);
				continue;
			}

			var host = await context.Config.GetStringAsync(entry.Id, BridgeConfigKeys.Host);
			bridges.Add(new ConfiguredBridge(
				entry.Id,
				HueClient.NormalizeBridgeId(bridgeId),
				entry.Title,
				string.IsNullOrWhiteSpace(host) ? null : host.Trim(),
				appKey));
		}

		_registry.Replace(bridges);
		_monitor.Start(context.Config);
		_logger.Information("Initialized with {Count} Hue bridge(s)", bridges.Count);
	}

	public Task ShutdownAsync()
	{
		_monitor.Stop();
		return Task.CompletedTask;
	}

	public IConfigFlow CreateConfigFlow() => new BridgeConfigFlow(_client, _discovery, _registry, _options, _logger);

	public Task<IReadOnlyList<IntegrationIssue>> GetIssuesAsync(CancellationToken cancellationToken = default)
	{
		var issues = new List<IntegrationIssue>();
		foreach (var bridge in _registry.All)
		{
			switch (bridge.Status)
			{
				case BridgeStatus.Unauthorized:
					issues.Add(new IntegrationIssue
					{
						Id = UnauthorizedIssuePrefix + bridge.BridgeId,
						Title = Strings.Issues.Unauthorized.Title(name: bridge.Title),
						Description = Strings.Issues.Unauthorized.Description(),
						Severity = IntegrationIssueSeverity.Error,
						ActionLabel = Strings.Issues.Unauthorized.Action(),
					});
					break;

				case BridgeStatus.Unreachable:
					issues.Add(new IntegrationIssue
					{
						Id = UnreachableIssuePrefix + bridge.BridgeId,
						Title = Strings.Issues.Unreachable.Title(name: bridge.Title),
						Description = Strings.Issues.Unreachable.Description(),
						Severity = IntegrationIssueSeverity.Warning,
						ActionLabel = MacroDeckStrings.Common.Retry(),
					});
					break;
			}
		}

		return Task.FromResult<IReadOnlyList<IntegrationIssue>>(issues);
	}

	public async Task<IssueResolution> ResolveIssueAsync(string issueId, CancellationToken cancellationToken = default)
	{
		if (issueId.StartsWith(UnauthorizedIssuePrefix, StringComparison.Ordinal))
		{
			return IssueResolution.Ok(followUp: IssueResolutionFollowUp.StartConfigFlow);
		}

		if (issueId.StartsWith(UnreachableIssuePrefix, StringComparison.Ordinal) &&
			_registry.Find(issueId[UnreachableIssuePrefix.Length..]) is { } bridge)
		{
			var probed = await _monitor.ProbeNowAsync(bridge, cancellationToken);
			return probed.Status == BridgeStatus.Online
				? IssueResolution.Ok(Strings.Issues.Unreachable.Resolved(name: bridge.Title))
				: IssueResolution.Failed(Strings.Issues.Unreachable.StillUnreachable(name: bridge.Title));
		}

		return IssueResolution.Failed(Strings.Issues.Unknown());
	}
}
