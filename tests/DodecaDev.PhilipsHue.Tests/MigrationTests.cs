using System.Text.Json;
using DodecaDev.PhilipsHue.Migration;
using MacroDeck.Sdk.Migration;
using NUnit.Framework;

namespace DodecaDev.PhilipsHue.Tests;

/// <summary>Configurations as the Macro Deck 2 plugin's Newtonsoft serializer wrote them.</summary>
[TestFixture]
public sealed class MigrationTests
{
	private const string IntegrationId = "com.dodecadev.philips-hue";

	private readonly MacroDeck2Migration _migration = new(IntegrationId);

	[Test]
	public async Task A_set_scene_button_keeps_its_bridge_scene_and_room()
	{
		var result = await MigrateAsync(MacroDeck2Migration.SetSceneTypeName,
			"""{"BridgeId":"001788FFFE123456","SceneId":"abc","GroupId":"1"}""");

		Assert.That(result!.ActionId, Is.EqualTo("set-scene"));
		Assert.That(result.IntegrationId, Is.EqualTo(IntegrationId));
		Assert.That(result.Parameters["bridge"].GetString(), Is.EqualTo("001788fffe123456"));
		Assert.That(result.Parameters["scene"].GetString(), Is.EqualTo("abc"));
		Assert.That(result.Parameters["room"].GetString(), Is.EqualTo("1"));
	}

	[Test]
	public async Task A_set_scene_button_without_a_scene_has_no_equivalent()
	{
		Assert.That(await MigrateAsync(MacroDeck2Migration.SetSceneTypeName, """{"BridgeId":"x"}"""), Is.Null);
	}

	[Test]
	public async Task An_update_light_button_converts_brightness_color_and_transition()
	{
		var result = await MigrateAsync(MacroDeck2Migration.UpdateLightTypeName,
			"""{"BridgeId":"001788fffe123456","LightIds":["1","3"],"color":"Red","isOn":true,"Brightness":255,"TransitionTime":"00:00:00.4000000"}""");

		Assert.That(result!.ActionId, Is.EqualTo("update-light"));
		Assert.That(result.Parameters["lights"].EnumerateArray().Select(item => item.GetString()), Is.EqualTo(new[] { "1", "3" }));
		Assert.That(result.Parameters["power"].GetString(), Is.EqualTo("on"));
		Assert.That(result.Parameters["brightness"].GetDouble(), Is.EqualTo(100));
		Assert.That(result.Parameters["color"].GetString(), Is.EqualTo("#FF0000"));
		Assert.That(result.Parameters["transitionTime"].GetDouble(), Is.EqualTo(400));
		Assert.That(result.Warnings, Is.Empty);
	}

	[TestCase("\"255, 128, 0\"", "#FF8000")]
	[TestCase("\"128, 255, 128, 0\"", "#FF8000")]
	[TestCase("\"ffff8000\"", "#FF8000")]
	[TestCase("{\"R\":255,\"G\":128,\"B\":0,\"A\":255,\"Name\":\"ffff8000\"}", "#FF8000")]
	[TestCase("{\"Name\":\"White\"}", "#FFFFFF")]
	public async Task Every_way_newtonsoft_wrote_a_color_is_read(string color, string expected)
	{
		var result = await MigrateAsync(MacroDeck2Migration.UpdateLightTypeName, $$"""{"LightIds":["1"],"color":{{color}}}""");

		Assert.That(result!.Parameters["color"].GetString(), Is.EqualTo(expected));
	}

	[Test]
	public async Task An_unreadable_color_is_dropped_with_a_warning()
	{
		var result = await MigrateAsync(MacroDeck2Migration.UpdateLightTypeName, """{"LightIds":["1"],"color":"no such color"}""");

		Assert.That(result!.Parameters.ContainsKey("color"), Is.False);
		Assert.That(result.Warnings, Has.Count.EqualTo(1));
	}

	[Test]
	public async Task A_light_switched_off_migrates_to_power_off()
	{
		var result = await MigrateAsync(MacroDeck2Migration.UpdateLightTypeName, """{"LightIds":["1"],"isOn":false}""");

		Assert.That(result!.Parameters["power"].GetString(), Is.EqualTo("off"));
	}

	[Test]
	public async Task An_adjust_light_button_keeps_its_percentages()
	{
		var result = await MigrateAsync(MacroDeck2Migration.AdjustLightTypeName,
			"""{"BridgeId":"b","LightIds":["2"],"BrightnessAdjustmentPercent":-25,"BrightnessAdjustment":-63,"HueAdjustmentPercent":10,"SaturationAdjustmentPercent":null}""");

		Assert.That(result!.ActionId, Is.EqualTo("adjust-light"));
		Assert.That(result.Parameters["brightnessChange"].GetDouble(), Is.EqualTo(-25));
		Assert.That(result.Parameters["hueChange"].GetDouble(), Is.EqualTo(10));
		Assert.That(result.Parameters.ContainsKey("saturationChange"), Is.False);
	}

	[Test]
	public async Task A_button_without_lights_migrates_with_a_warning()
	{
		var result = await MigrateAsync(MacroDeck2Migration.AdjustLightTypeName, """{"BrightnessAdjustmentPercent":10}""");

		Assert.That(result!.Warnings, Has.Count.EqualTo(1));
	}

	[TestCase("SomeOther.Plugin.Action", """{"SceneId":"abc"}""")]
	[TestCase(MacroDeck2Migration.SetSceneTypeName, "not json")]
	[TestCase(MacroDeck2Migration.SetSceneTypeName, "")]
	public async Task Unknown_or_unreadable_actions_have_no_equivalent(string typeName, string configuration)
	{
		Assert.That(await MigrateAsync(typeName, configuration), Is.Null);
	}

	[Test]
	public async Task Every_paired_bridge_becomes_a_config_entry_with_its_key_as_a_secret()
	{
		var settings = new ForeignPluginSettings(
			"recklessboon_philips hue plugin",
			new Dictionary<string, string>(),
			[new Dictionary<string, string> { ["001788FFFE123456"] = "key-1", ["001788fffe999999"] = "key-2", ["empty"] = "" }],
			[]);

		var entries = await _migration.MigrateConfigurationAsync(settings, CancellationToken.None);

		Assert.That(entries, Has.Count.EqualTo(2));
		Assert.That(entries[0].Values["bridge_id"].GetString(), Is.EqualTo("001788fffe123456"));
		Assert.That(entries[0].Values.ContainsKey("host"), Is.False);
		Assert.That(entries[0].Secrets["app_key"].Value, Is.EqualTo("key-1"));
		Assert.That(entries[0].Secrets["app_key"].Kind, Is.EqualTo(MigratedSecretKind.Secret));
	}

	[Test]
	public async Task Declined_credentials_create_no_entries()
	{
		var settings = new ForeignPluginSettings("recklessboon_philips hue plugin", new Dictionary<string, string>(), [], []);

		Assert.That(await _migration.MigrateConfigurationAsync(settings, CancellationToken.None), Is.Empty);
	}

	private Task<ActionMigrationResult?> MigrateAsync(string typeName, string configuration)
		=> _migration.MigrateActionAsync(
			new ForeignAction(typeName, "Philips Hue Plugin", null!, configuration, null!),
			CancellationToken.None);
}
