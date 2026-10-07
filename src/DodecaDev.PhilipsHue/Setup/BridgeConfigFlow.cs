using DodecaDev.PhilipsHue.Actions;
using DodecaDev.PhilipsHue.Bridges;
using DodecaDev.PhilipsHue.Hue;
using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.ConfigFlow;
using Microsoft.Extensions.Options;
using Serilog;

namespace DodecaDev.PhilipsHue.Setup;

/// <summary>
/// Pairs one bridge per config entry: pick a discovered bridge or type its address, then press the link
/// button so the bridge hands out an app key. The key is stored as a secret.
/// </summary>
internal sealed class BridgeConfigFlow(
	HueClient client,
	HueDiscovery discovery,
	BridgeRegistry registry,
	IOptions<HueOptions> options,
	ILogger logger) : IConfigFlow
{
	internal const string BridgeStepId = "bridge";
	internal const string LinkStepId = "link";
	internal const string AddressField = "bridgeAddress";
	internal const string ManualAddressField = "manualAddress";
	internal const string ManualChoice = "manual";

	// The v1 API allows 20 characters for the application and 19 for the device in "app#device".
	private const string ApplicationName = "macro_deck";
	private const int MaxDeviceNameLength = 19;

	private readonly ILogger _logger = logger.ForContext<BridgeConfigFlow>();

	private IReadOnlyList<(DiscoveredBridge Bridge, string Name)> _discovered = [];
	private string? _host;
	private HueBridgeInfo? _bridge;

	public async Task<ConfigFlowResult> StartAsync(IConfigFlowContext context, CancellationToken cancellationToken)
	{
		_discovered = await DiscoverAsync(cancellationToken);
		return ConfigFlowResult.Step(BridgeStep());
	}

	public async Task<ConfigFlowResult> SubmitAsync(
		string stepId,
		IReadOnlyDictionary<string, object?> input,
		IConfigFlowContext context,
		CancellationToken cancellationToken)
		=> stepId switch
		{
			BridgeStepId => await SubmitBridgeAsync(input, context, cancellationToken),
			LinkStepId => await SubmitLinkAsync(cancellationToken),
			_ => ConfigFlowResult.Error(BridgeStep(), Strings.Setup.UnknownStep()),
		};

	private async Task<ConfigFlowResult> SubmitBridgeAsync(
		IReadOnlyDictionary<string, object?> input, IConfigFlowContext context, CancellationToken cancellationToken)
	{
		var choice = ParameterValues.String(input, AddressField);
		var manual = choice is null or ManualChoice;
		var fieldName = manual ? ManualAddressField : AddressField;
		var host = manual ? ParameterValues.String(input, ManualAddressField) : choice;

		if (string.IsNullOrEmpty(host) || Uri.CheckHostName(host) == UriHostNameType.Unknown)
		{
			var message = MacroDeckStrings.Validation.InvalidIpAddress(Strings.Setup.Bridge.ManualAddress.Label());
			return ConfigFlowResult.Error(BridgeStep(), message, new Dictionary<string, LocalizedText> { [fieldName] = message });
		}

		HueBridgeInfo bridge;
		try
		{
			bridge = await client.GetBridgeInfoAsync(host, cancellationToken);
		}
		catch (HueException exception)
		{
			_logger.Information(exception, "No Hue bridge answered at {Host}", host);
			var message = Strings.Setup.Bridge.NotABridge(address: host);
			return ConfigFlowResult.Error(BridgeStep(), message, new Dictionary<string, LocalizedText> { [fieldName] = message });
		}

		var reconfiguring = (context as IConfigFlowEntryContext)?.EntryTitle is not null;
		if (!reconfiguring && registry.Find(bridge.BridgeId) is { } existing)
		{
			return ConfigFlowResult.Error(BridgeStep(), Strings.Setup.Bridge.AlreadyConfigured(name: existing.Title));
		}

		_host = host;
		_bridge = bridge;
		return ConfigFlowResult.Step(LinkStep(bridge));
	}

	private async Task<ConfigFlowResult> SubmitLinkAsync(CancellationToken cancellationToken)
	{
		if (_host is not { } host || _bridge is not { } bridge)
		{
			return ConfigFlowResult.Error(BridgeStep(), Strings.Setup.SessionLost());
		}

		var settings = options.Value;
		var attempts = settings.LinkRetryInterval > TimeSpan.Zero
			? Math.Max(1, (int)Math.Ceiling(settings.LinkButtonWindow / settings.LinkRetryInterval))
			: 1;
		for (var attempt = 1; ; attempt++)
		{
			try
			{
				var appKey = await client.RegisterAsync(host, DeviceType(), cancellationToken);
				_logger.Information("Paired with Hue bridge {BridgeId} at {Host}", bridge.BridgeId, host);

				// The entry title is a plain string by design: the host stores it as the entry's name.
				return ConfigFlowResult.Complete(bridge.Name, new Dictionary<string, ConfigFlowValue>
				{
					[BridgeConfigKeys.BridgeId] = ConfigFlowValue.Plain(bridge.BridgeId),
					[BridgeConfigKeys.Host] = ConfigFlowValue.Plain(host),
					[BridgeConfigKeys.AppKey] = ConfigFlowValue.Secret(appKey),
				});
			}
			catch (HueException exception) when (exception.Reason == HueFailure.LinkButtonNotPressed)
			{
				if (attempt >= attempts)
				{
					return ConfigFlowResult.Error(LinkStep(bridge), Strings.Setup.Link.NotPressed());
				}
			}
			catch (HueException exception)
			{
				_logger.Warning(exception, "Pairing with Hue bridge {BridgeId} failed", bridge.BridgeId);
				return ConfigFlowResult.Error(LinkStep(bridge), exception.UserMessage);
			}

			await Task.Delay(settings.LinkRetryInterval, cancellationToken);
		}
	}

	private async Task<IReadOnlyList<(DiscoveredBridge Bridge, string Name)>> DiscoverAsync(CancellationToken cancellationToken)
	{
		IReadOnlyList<DiscoveredBridge> found;
		try
		{
			found = await discovery.DiscoverAsync(cancellationToken);
		}
		catch (HueException exception)
		{
			_logger.Information(exception, "Hue bridge discovery failed; offering manual entry only");
			return [];
		}

		return await Task.WhenAll(found.Select(async bridge =>
		{
			try
			{
				return (bridge, (await client.GetBridgeInfoAsync(bridge.Host, cancellationToken)).Name);
			}
			catch (HueException)
			{
				return (bridge, bridge.BridgeId);
			}
		}));
	}

	private ConfigFlowStep BridgeStep()
	{
		var choices = _discovered
			.Select(found => new ActionParameterOption
			{
				Value = found.Bridge.Host,
				Label = Strings.Setup.Bridge.Discovered(name: found.Name, address: found.Bridge.Host),
			})
			.Append(new ActionParameterOption { Value = ManualChoice, Label = Strings.Setup.Bridge.Manual() })
			.ToList();

		return new ConfigFlowStep
		{
			StepId = BridgeStepId,
			Title = Strings.Setup.Bridge.Title(),
			Description = _discovered.Count > 0
				? Strings.Setup.Bridge.Description()
				: Strings.Setup.Bridge.NoneDiscovered(),
			Fields =
			[
				ActionParameter.Choice(
					AddressField,
					choices,
					label: Strings.Setup.Bridge.Address.Label(),
					defaultValue: choices[0].Value,
					required: true),
				ActionParameter.Text(
					ManualAddressField,
					label: Strings.Setup.Bridge.ManualAddress.Label(),
					description: Strings.Setup.Bridge.ManualAddress.Description(),
					placeholder: "192.168.1.20",
					required: true).OnlyWhen(AddressField, [ManualChoice]),
			],
		};
	}

	private static ConfigFlowStep LinkStep(HueBridgeInfo bridge) => new()
	{
		StepId = LinkStepId,
		Title = Strings.Setup.Link.Title(),
		Description = Strings.Setup.Link.Description(name: bridge.Name),
		Instructions =
		[
			new ConfigFlowInstruction { Text = Strings.Setup.Link.PressButton() },
			new ConfigFlowInstruction { Text = Strings.Setup.Link.SelectContinue() },
		],
		Fields = [],
	};

	private static string DeviceType()
	{
		var machine = Environment.MachineName;
		return $"{ApplicationName}#{machine[..Math.Min(machine.Length, MaxDeviceNameLength)]}";
	}
}
