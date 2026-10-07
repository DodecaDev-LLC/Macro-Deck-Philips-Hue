using DodecaDev.PhilipsHue.Hue;
using MacroDeck.Localization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;

namespace DodecaDev.PhilipsHue.Actions;

/// <summary>Sets one or more lights to a fixed state: power, brightness and color.</summary>
public sealed class UpdateLightAction(HueActionSupport support) : IActionDefinition, IDynamicOptionsActionDefinition
{
	public const string ActionId = "update-light";
	public const string PowerParameter = "power";
	public const string BrightnessParameter = "brightness";
	public const string ColorParameter = "color";
	public const string TransitionParameter = "transitionTime";

	public const string PowerOn = "on";
	public const string PowerOff = "off";
	public const string PowerUnchanged = "unchanged";

	public const double DefaultTransitionMilliseconds = 400;

	public string Id => ActionId;

	public LocalizedText Name => Strings.Actions.UpdateLight.Name();

	public LocalizedText Description => Strings.Actions.UpdateLight.Description();

	public IReadOnlyList<ActionParameter> Parameters { get; } =
	[
		HueActionSupport.BridgeField(),
		HueActionSupport.LightsField(),
		ActionParameter.Choice(
			PowerParameter,
			[
				new ActionParameterOption { Value = PowerOn, Label = MacroDeckStrings.States.On() },
				new ActionParameterOption { Value = PowerOff, Label = MacroDeckStrings.States.Off() },
				new ActionParameterOption { Value = PowerUnchanged, Label = Strings.Actions.UpdateLight.Power.Unchanged() },
			],
			label: Strings.Actions.UpdateLight.Power.Label(),
			defaultValue: PowerOn,
			required: true),
		ActionParameter.Slider(
			BrightnessParameter,
			1,
			100,
			label: Strings.Actions.UpdateLight.Brightness.Label(),
			description: Strings.Actions.UpdateLight.Brightness.Description(),
			step: 1,
			defaultValue: 100).OnlyWhen(PowerParameter, [PowerOn, PowerUnchanged]),
		ActionParameter.Color(
			ColorParameter,
			label: Strings.Actions.UpdateLight.Color.Label(),
			description: Strings.Actions.UpdateLight.Color.Description(),
			supportsReset: true).OnlyWhen(PowerParameter, [PowerOn, PowerUnchanged]),
		ActionParameter.Duration(
			TransitionParameter,
			label: Strings.Parameters.TransitionTime.Label(),
			description: Strings.Parameters.TransitionTime.Description(),
			min: 0,
			max: 600_000,
			defaultMilliseconds: DefaultTransitionMilliseconds),
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

	/// <summary>Builds the state to send. Hidden parameters are still sent by the host, so a light being
	/// switched off ignores brightness and color here rather than trusting the editor to have dropped them.</summary>
	internal static HueLightState? BuildState(IReadOnlyDictionary<string, object> parameters, out ActionResult? invalid)
	{
		invalid = null;
		var power = ParameterValues.String(parameters, PowerParameter) ?? PowerOn;
		if (power is not (PowerOn or PowerOff or PowerUnchanged))
		{
			invalid = ActionResult.Failed(ActionErrorCodes.InvalidParameter,
				MacroDeckStrings.Validation.InvalidValue(Strings.Actions.UpdateLight.Power.Label()));
			return null;
		}

		var transitionMilliseconds = ParameterValues.Number(parameters, TransitionParameter) ?? DefaultTransitionMilliseconds;
		var transitionTime = (int)Math.Round(Math.Max(transitionMilliseconds, 0) / 100);

		if (power == PowerOff)
		{
			return new HueLightState { On = false, TransitionTime = transitionTime };
		}

		(double X, double Y)? xy = null;
		if (ParameterValues.String(parameters, ColorParameter) is { } colorText)
		{
			if (!HueColor.TryParseHex(colorText, out var rgb))
			{
				invalid = ActionResult.Failed(ActionErrorCodes.InvalidParameter,
					MacroDeckStrings.Validation.InvalidValue(Strings.Actions.UpdateLight.Color.Label()));
				return null;
			}

			xy = HueColor.ToXy(rgb.R, rgb.G, rgb.B);
		}

		var brightnessPercent = Math.Clamp(ParameterValues.Number(parameters, BrightnessParameter) ?? 100, 1, 100);

		return new HueLightState
		{
			On = power == PowerOn ? true : null,
			Brightness = (int)Math.Round(brightnessPercent * 2.54),
			Xy = xy,
			TransitionTime = transitionTime,
		};
	}

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

			if (BuildState(context.Parameters, out var invalid) is not { } state)
			{
				return Task.FromResult(invalid!);
			}

			return support.RunAsync(context.Parameters, (bridge, host) =>
				support.WriteLightsAsync(bridge, host, lights, state, context.CancellationToken));
		}
	}
}
