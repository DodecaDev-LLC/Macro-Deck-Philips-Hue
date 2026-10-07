using DodecaDev.PhilipsHue.Bridges;
using DodecaDev.PhilipsHue.Hue;
using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;

namespace DodecaDev.PhilipsHue.Actions;

/// <summary>What every Hue action shares: finding the bridge, talking to it, and the option lists.</summary>
public sealed class HueActionSupport(BridgeRegistry registry, BridgeMonitor monitor, HueClient client)
{
	public const string BridgeParameter = "bridge";
	public const string LightsParameter = "lights";

	public HueClient Client => client;

	public static ActionParameter BridgeField() => ActionParameter.DynamicChoice(
		BridgeParameter,
		label: Strings.Parameters.Bridge.Label(),
		description: Strings.Parameters.Bridge.Description(),
		placeholder: Strings.Parameters.Bridge.Placeholder());

	public static ActionParameter LightsField() => ActionParameter.MultiSelect(
		LightsParameter,
		label: Strings.Parameters.Lights.Label(),
		description: Strings.Parameters.Lights.Description(),
		required: true);

	public Task<DynamicOptionsResult> LightOptionsAsync(DynamicOptionsContext context, CancellationToken cancellationToken)
		=> OptionsFromBridgeAsync(context, async (bridge, host, ct) =>
			(await client.GetLightsAsync(host, bridge.AppKey, ct)).Select(light => (light.Id, light.Name)),
			cancellationToken);

	/// <summary>Sends one state to every light at once. The v1 API has no multi-light write outside groups.</summary>
	public async Task<ActionResult> WriteLightsAsync(
		ConfiguredBridge bridge, string host, IReadOnlyList<string> lightIds, HueLightState state, CancellationToken cancellationToken)
	{
		var results = await Task.WhenAll(lightIds.Select(lightId =>
			client.SetLightStateAsync(host, bridge.AppKey, lightId, state, cancellationToken)));
		return FromWriteErrors(results.SelectMany(errors => errors));
	}

	/// <summary>
	/// Runs <paramref name="operation"/> against the bridge the parameters name. Every bridge failure becomes
	/// a failed result with the matching error code, and an unreachable bridge is re-probed in the background
	/// in case it moved to a new address.
	/// </summary>
	public async Task<ActionResult> RunAsync(
		IReadOnlyDictionary<string, object> parameters,
		Func<ConfiguredBridge, string, Task<ActionResult>> operation)
	{
		var lookup = registry.Resolve(ParameterValues.String(parameters, BridgeParameter));
		if (lookup.Bridge is not { } bridge)
		{
			return LookupFailure(lookup.Failure);
		}

		if (bridge.Status == BridgeStatus.Unauthorized)
		{
			return ActionResult.Failed(ActionErrorCodes.PermissionDenied, Strings.Failures.Unauthorized());
		}

		if (bridge.Host is not { Length: > 0 } host)
		{
			monitor.RequestProbe(bridge);
			return ActionResult.Failed(ActionErrorCodes.NotConnected, Strings.Failures.BridgeNotFound());
		}

		try
		{
			return await operation(bridge, host);
		}
		catch (HueException exception)
		{
			if (exception.Reason is HueFailure.Unreachable or HueFailure.Timeout or HueFailure.Unauthorized)
			{
				monitor.RequestProbe(bridge);
			}

			return ToResult(exception);
		}
	}

	/// <summary>
	/// Turns the per-item errors of one or more writes into a result. A parameter the device does not have
	/// (color on a white bulb) is not a failure: the bridge applied everything else.
	/// </summary>
	public static ActionResult FromWriteErrors(IEnumerable<HueApiError> errors)
	{
		var relevant = errors.Where(error => error.Type != HueApiError.ParameterNotAvailable).ToList();
		if (relevant.Count == 0)
		{
			return ActionResult.Success();
		}

		if (relevant.Any(error => error.Type == HueApiError.ResourceNotAvailable))
		{
			return ActionResult.Failed(ActionErrorCodes.NotFound, Strings.Failures.ResourceGone());
		}

		if (relevant.Any(error => error.Type == HueApiError.DeviceIsOff))
		{
			return ActionResult.Failed(ActionErrorCodes.ProviderRejected, Strings.Failures.LightIsOff());
		}

		return ActionResult.Failed(ActionErrorCodes.ProviderRejected, Strings.Failures.Rejected());
	}

	public DynamicOptionsResult BridgeOptions(string? filter)
	{
		var bridges = registry.All;
		if (bridges.Count == 0)
		{
			return new DynamicOptionsResult { Options = [], Error = Strings.Failures.NoBridgeConfigured() };
		}

		return new DynamicOptionsResult
		{
			Options = Filtered(bridges.Select(bridge => (bridge.BridgeId, bridge.Title)), filter),
		};
	}

	/// <summary>Builds an option list from the bridge the draft names, explaining an empty list instead of
	/// returning one silently.</summary>
	public async Task<DynamicOptionsResult> OptionsFromBridgeAsync(
		DynamicOptionsContext context,
		Func<ConfiguredBridge, string, CancellationToken, Task<IEnumerable<(string Value, string Label)>>> load,
		CancellationToken cancellationToken)
	{
		var lookup = registry.Resolve(ParameterValues.String(context.CurrentParameters, BridgeParameter));
		if (lookup.Bridge is not { } bridge)
		{
			return new DynamicOptionsResult { Options = [], Error = LookupMessage(lookup.Failure) };
		}

		if (bridge.Host is not { Length: > 0 } host)
		{
			monitor.RequestProbe(bridge);
			return new DynamicOptionsResult { Options = [], Error = Strings.Failures.BridgeNotFound() };
		}

		try
		{
			var items = await load(bridge, host, cancellationToken);
			return new DynamicOptionsResult { Options = Filtered(items, context.Filter), CacheSeconds = 15 };
		}
		catch (HueException exception)
		{
			if (exception.Reason is HueFailure.Unreachable or HueFailure.Timeout or HueFailure.Unauthorized)
			{
				monitor.RequestProbe(bridge);
			}

			return new DynamicOptionsResult { Options = [], Error = exception.UserMessage };
		}
	}

	public static ActionResult ToResult(HueException exception) => ActionResult.Failed(exception.Reason switch
	{
		HueFailure.Unreachable => ActionErrorCodes.NotConnected,
		HueFailure.Timeout => ActionErrorCodes.Timeout,
		HueFailure.Unauthorized => ActionErrorCodes.PermissionDenied,
		HueFailure.Rejected => ActionErrorCodes.ProviderRejected,
		_ => ActionErrorCodes.ProviderError,
	}, exception.UserMessage);

	private static ActionResult LookupFailure(BridgeLookupFailure failure) => failure switch
	{
		BridgeLookupFailure.NoneConfigured => ActionResult.Failed(ActionErrorCodes.NotConfigured, LookupMessage(failure)),
		BridgeLookupFailure.Ambiguous => ActionResult.Failed(ActionErrorCodes.InvalidParameter, LookupMessage(failure)),
		_ => ActionResult.Failed(ActionErrorCodes.NotFound, LookupMessage(failure)),
	};

	private static LocalizedText LookupMessage(BridgeLookupFailure failure) => failure switch
	{
		BridgeLookupFailure.NoneConfigured => Strings.Failures.NoBridgeConfigured(),
		BridgeLookupFailure.Ambiguous => Strings.Failures.ChooseBridge(),
		_ => Strings.Failures.UnknownBridge(),
	};

	private static List<ActionParameterOption> Filtered(IEnumerable<(string Value, string Label)> items, string? filter)
		=> [.. items
			.Where(item => string.IsNullOrWhiteSpace(filter) || item.Label.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
			.OrderBy(item => item.Label, StringComparer.CurrentCultureIgnoreCase)
			.Select(item => new ActionParameterOption { Value = item.Value, Label = LocalizedText.FromLiteral(item.Label) })];
}
