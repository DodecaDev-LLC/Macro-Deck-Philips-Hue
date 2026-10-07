using System.Globalization;
using System.Text.Json;
using DodecaDev.PhilipsHue.Actions;
using DodecaDev.PhilipsHue.Bridges;
using DodecaDev.PhilipsHue.Hue;
using MacroDeck.Localization;
using MacroDeck.Sdk.Migration;

namespace DodecaDev.PhilipsHue.Migration;

/// <summary>
/// Takes over the Macro Deck 2 "Philips Hue Plugin" by RecklessBoon: its paired bridges and its three
/// actions. Macro Deck 2 stored each action's configuration as Newtonsoft JSON of the old config classes,
/// so the property names below are that plugin's, not this one's.
/// </summary>
public sealed class MacroDeck2Migration(string integrationId) : IIntegrationMigration
{
	internal const string SetSceneTypeName = "RecklessBoon.MacroDeck.PhilipsHuePlugin.Actions.SetSceneAction";
	internal const string UpdateLightTypeName = "RecklessBoon.MacroDeck.PhilipsHuePlugin.Actions.UpdateLightAction";
	internal const string AdjustLightTypeName = "RecklessBoon.MacroDeck.PhilipsHuePlugin.Actions.AdjustLightAction";

	public MigrationSource Source => MigrationSource.MacroDeck2;

	// The assembly name of the Macro Deck 2 plugin.
	public IReadOnlyList<string> ClaimedActionSources { get; } = ["Philips Hue Plugin"];

	// Macro Deck 2's settings file name: "<author>_<plugin name>", lowercased.
	public IReadOnlyList<string> ClaimedSettingsSources { get; } = ["recklessboon_philips hue plugin"];

	public Task<ActionMigrationResult?> MigrateActionAsync(ForeignAction action, CancellationToken cancellationToken)
	{
		JsonElement configuration;
		try
		{
			using var document = JsonDocument.Parse(action.Configuration ?? string.Empty);
			configuration = document.RootElement.Clone();
		}
		catch (JsonException)
		{
			return Task.FromResult<ActionMigrationResult?>(null);
		}

		if (configuration.ValueKind != JsonValueKind.Object)
		{
			return Task.FromResult<ActionMigrationResult?>(null);
		}

		return Task.FromResult(action.TypeName switch
		{
			SetSceneTypeName => MigrateSetScene(action, configuration),
			UpdateLightTypeName => MigrateUpdateLight(action, configuration),
			AdjustLightTypeName => MigrateAdjustLight(action, configuration),
			_ => null,
		});
	}

	/// <summary>
	/// Macro Deck 2 kept one credential set holding every paired bridge, keyed by bridge id with the app key
	/// as value. It never stored addresses: each entry is found again by discovery on first start.
	/// </summary>
	public Task<IReadOnlyList<MigratedConfiguration>> MigrateConfigurationAsync(
		ForeignPluginSettings settings, CancellationToken cancellationToken)
	{
		var results = new List<MigratedConfiguration>();
		var seen = new HashSet<string>(StringComparer.Ordinal);

		foreach (var credentials in settings.Credentials)
		{
			foreach (var (key, value) in credentials)
			{
				if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
				{
					continue;
				}

				var bridgeId = HueClient.NormalizeBridgeId(key);
				if (!seen.Add(bridgeId))
				{
					continue;
				}

				results.Add(new MigratedConfiguration(
					integrationId,
					$"Hue bridge {bridgeId}",
					new Dictionary<string, JsonElement> { [BridgeConfigKeys.BridgeId] = JsonSerializer.SerializeToElement(bridgeId) },
					new Dictionary<string, MigratedSecret> { [BridgeConfigKeys.AppKey] = new(value, MigratedSecretKind.Secret) }));
			}
		}

		return Task.FromResult<IReadOnlyList<MigratedConfiguration>>(results);
	}

	private ActionMigrationResult? MigrateSetScene(ForeignAction action, JsonElement configuration)
	{
		if (ReadString(configuration, "SceneId") is not { } sceneId)
		{
			return null;
		}

		var parameters = BridgeParameter(configuration);
		parameters[SetSceneAction.SceneParameter] = JsonSerializer.SerializeToElement(sceneId);
		if (ReadString(configuration, "GroupId") is { } groupId)
		{
			parameters[SetSceneAction.RoomParameter] = JsonSerializer.SerializeToElement(groupId);
		}

		return new ActionMigrationResult(integrationId, SetSceneAction.ActionId, Label(action, "Set scene"), parameters);
	}

	private ActionMigrationResult MigrateUpdateLight(ForeignAction action, JsonElement configuration)
	{
		var warnings = new List<LocalizedText>();
		var parameters = BridgeParameter(configuration);
		AddLights(configuration, parameters, warnings);

		var isOn = ReadBool(configuration, "isOn");
		parameters[UpdateLightAction.PowerParameter] = JsonSerializer.SerializeToElement(
			isOn == false ? UpdateLightAction.PowerOff : UpdateLightAction.PowerOn);

		if (ReadNumber(configuration, "Brightness") is { } brightness)
		{
			// Macro Deck 2 stored 0-255; this plugin takes a percentage.
			var percent = Math.Clamp(Math.Round(brightness / 255 * 100), 1, 100);
			parameters[UpdateLightAction.BrightnessParameter] = JsonSerializer.SerializeToElement(percent);
		}

		if (TryGetProperty(configuration, "color", out var color) && color.ValueKind != JsonValueKind.Null)
		{
			if (LegacyColor.TryRead(color, out var hex))
			{
				parameters[UpdateLightAction.ColorParameter] = JsonSerializer.SerializeToElement(hex);
			}
			else
			{
				warnings.Add(Strings.Migration.ColorNotMigrated());
			}
		}

		if (ReadString(configuration, "TransitionTime") is { } transition &&
			TimeSpan.TryParse(transition, CultureInfo.InvariantCulture, out var transitionTime))
		{
			parameters[UpdateLightAction.TransitionParameter] = JsonSerializer.SerializeToElement(transitionTime.TotalMilliseconds);
		}

		return new ActionMigrationResult(integrationId, UpdateLightAction.ActionId, Label(action, "Update light"), parameters, warnings);
	}

	private ActionMigrationResult MigrateAdjustLight(ForeignAction action, JsonElement configuration)
	{
		var warnings = new List<LocalizedText>();
		var parameters = BridgeParameter(configuration);
		AddLights(configuration, parameters, warnings);

		AddPercent(configuration, "BrightnessAdjustmentPercent", AdjustLightAction.BrightnessParameter, parameters);
		AddPercent(configuration, "SaturationAdjustmentPercent", AdjustLightAction.SaturationParameter, parameters);
		AddPercent(configuration, "HueAdjustmentPercent", AdjustLightAction.HueParameter, parameters);
		AddPercent(configuration, "ColorTemperatureAdjustmentPercent", AdjustLightAction.ColorTemperatureParameter, parameters);

		return new ActionMigrationResult(integrationId, AdjustLightAction.ActionId, Label(action, "Adjust light"), parameters, warnings);
	}

	private static Dictionary<string, JsonElement> BridgeParameter(JsonElement configuration)
	{
		var parameters = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
		if (ReadString(configuration, "BridgeId") is { } bridgeId)
		{
			parameters[HueActionSupport.BridgeParameter] = JsonSerializer.SerializeToElement(HueClient.NormalizeBridgeId(bridgeId));
		}

		return parameters;
	}

	private static void AddLights(JsonElement configuration, Dictionary<string, JsonElement> parameters, List<LocalizedText> warnings)
	{
		var lights = TryGetProperty(configuration, "LightIds", out var array) && array.ValueKind == JsonValueKind.Array
			? array.EnumerateArray()
				.Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.ValueKind == JsonValueKind.Number ? item.GetRawText() : null)
				.OfType<string>()
				.ToList()
			: [];

		if (lights.Count == 0)
		{
			warnings.Add(Strings.Migration.NoLightsSelected());
		}

		parameters[HueActionSupport.LightsParameter] = JsonSerializer.SerializeToElement(lights);
	}

	private static void AddPercent(JsonElement configuration, string legacyName, string parameterName, Dictionary<string, JsonElement> parameters)
	{
		if (ReadNumber(configuration, legacyName) is { } percent)
		{
			parameters[parameterName] = JsonSerializer.SerializeToElement(Math.Clamp(percent, -100, 100));
		}
	}

	private static string Label(ForeignAction action, string fallback)
		=> string.IsNullOrWhiteSpace(action.DisplayName) ? fallback : action.DisplayName;

	private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
	{
		foreach (var property in element.EnumerateObject())
		{
			if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
			{
				value = property.Value;
				return true;
			}
		}

		value = default;
		return false;
	}

	private static string? ReadString(JsonElement element, string name)
		=> TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
			? text
			: null;

	private static double? ReadNumber(JsonElement element, string name)
		=> TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

	private static bool? ReadBool(JsonElement element, string name)
		=> TryGetProperty(element, name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
			? value.GetBoolean()
			: null;
}
