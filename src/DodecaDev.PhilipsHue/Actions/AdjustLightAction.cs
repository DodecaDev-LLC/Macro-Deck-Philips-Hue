using DodecaDev.PhilipsHue.Hue;
using MacroDeck.Localization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;

namespace DodecaDev.PhilipsHue.Actions;

/// <summary>Moves lights relative to their current state: brighter, more saturated, a hue shift, warmer.</summary>
public sealed class AdjustLightAction(HueActionSupport support) : IActionDefinition, IDynamicOptionsActionDefinition
{
	public const string ActionId = "adjust-light";
	public const string BrightnessParameter = "brightnessChange";
	public const string SaturationParameter = "saturationChange";
	public const string HueParameter = "hueChange";
	public const string ColorTemperatureParameter = "colorTemperatureChange";

	// Percent of each v1 range, the same scale the Macro Deck 2 plugin used so migrated buttons keep their effect.
	private const double BrightnessPerPercent = 2.54;
	private const double SaturationPerPercent = 2.54;
	private const double HuePerPercent = 655.34;
	private const double ColorTemperaturePerPercent = 3.5;

	public string Id => ActionId;

	public LocalizedText Name => Strings.Actions.AdjustLight.Name();

	public LocalizedText Description => Strings.Actions.AdjustLight.Description();

	public IReadOnlyList<ActionParameter> Parameters { get; } =
	[
		HueActionSupport.BridgeField(),
		HueActionSupport.LightsField(),
		Change(BrightnessParameter, Strings.Actions.AdjustLight.Brightness.Label()),
		Change(SaturationParameter, Strings.Actions.AdjustLight.Saturation.Label()),
		Change(HueParameter, Strings.Actions.AdjustLight.Hue.Label()),
		Change(ColorTemperatureParameter, Strings.Actions.AdjustLight.ColorTemperature.Label()),
	];

	public MacroDeckPlatform Platforms => MacroDeckPlatform.All;

	public IActionExecutor CreateExecutor() => new Executor(support);

	public Task<DynamicOptionsResult> GetDynamicOptionsAsync(DynamicOptionsContext context, CancellationToken cancellationToken)
		=> context.ParameterName switch
		{
			HueActionSupport.BridgeParameter => Task.FromResult(support.BridgeOptions(context.Filter)),
			HueActionSupport.LightsParameter => support.LightOptionsAsync(context, cancellationToken),
			_ => Task.FromResult(new DynamicOptionsResult { Options = [] }),
		};

	internal static HueLightState BuildState(IReadOnlyDictionary<string, object> parameters) => new()
	{
		BrightnessIncrement = Increment(parameters, BrightnessParameter, BrightnessPerPercent),
		SaturationIncrement = Increment(parameters, SaturationParameter, SaturationPerPercent),
		HueIncrement = Increment(parameters, HueParameter, HuePerPercent),
		ColorTemperatureIncrement = Increment(parameters, ColorTemperatureParameter, ColorTemperaturePerPercent),
	};

	private static int? Increment(IReadOnlyDictionary<string, object> parameters, string name, double perPercent)
	{
		var percent = Math.Clamp(ParameterValues.Number(parameters, name) ?? 0, -100, 100);
		var increment = (int)(percent * perPercent);
		return increment == 0 ? null : increment;
	}

	private static ActionParameter Change(string name, LocalizedText label) => ActionParameter.Slider(
		name,
		-100,
		100,
		label: label,
		description: Strings.Actions.AdjustLight.Change.Description(),
		step: 1,
		defaultValue: 0);

	private sealed class Executor(HueActionSupport support) : IActionExecutor
	{
		public Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
		{
			var lights = ParameterValues.List(context.Parameters, HueActionSupport.LightsParameter);
			if (lights.Count == 0)
			{
				return Task.FromResult(ActionResult.Failed(ActionErrorCodes.InvalidParameter,
					MacroDeckStrings.Validation.Required(Strings.Parameters.Lights.Label())));
			}

			var state = BuildState(context.Parameters);
			if (state.IsEmpty)
			{
				// Every change left at zero: nothing to adjust, which is a legitimate no-op.
				return ActionResult.SucceededTask;
			}

			return support.RunAsync(context.Parameters, (bridge, host) =>
				support.WriteLightsAsync(bridge, host, lights, state, context.CancellationToken));
		}
	}
}
