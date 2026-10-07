using MacroDeck.Plugin.Protocol.Capabilities.Actions;
using NUnit.Framework;

namespace DodecaDev.PhilipsHue.Tests;

[TestFixture]
public sealed class ActionTests
{
	[Test]
	public async Task An_action_before_any_bridge_is_set_up_says_so()
	{
		await using var harness = Harness.Create(new FakeHueBridge());
		await harness.InitializeIntegrationsAsync();

		var outcome = await harness.Actions.ExecuteAsync("set-scene", Parameters(("scene", "abc")));

		Assert.That(outcome.Succeeded, Is.False);
		Assert.That(outcome.Error!.Message, Does.Contain("No Hue bridge is set up"));
	}

	[Test]
	public async Task Set_scene_recalls_on_all_lights_when_no_room_is_chosen()
	{
		var bridge = new FakeHueBridge();
		await using var harness = await Harness.ConfiguredAsync(bridge);

		var outcome = await harness.Actions.ExecuteAsync("set-scene", Parameters(("scene", "abc")));

		Assert.That(outcome.Succeeded, Is.True);
		var write = bridge.Writes.Single();
		Assert.That(write.Path, Is.EqualTo("/api/valid-key/groups/0/action"));
		Assert.That(write.Body!["scene"]!.GetValue<string>(), Is.EqualTo("abc"));
	}

	[Test]
	public async Task Set_scene_uses_the_chosen_room()
	{
		var bridge = new FakeHueBridge();
		await using var harness = await Harness.ConfiguredAsync(bridge);

		await harness.Actions.ExecuteAsync("set-scene", Parameters(("scene", "abc"), ("room", "1")));

		Assert.That(bridge.Writes.Single().Path, Is.EqualTo("/api/valid-key/groups/1/action"));
	}

	[Test]
	public async Task Update_light_sends_power_brightness_color_and_transition_to_every_light()
	{
		var bridge = new FakeHueBridge();
		await using var harness = await Harness.ConfiguredAsync(bridge);

		var outcome = await harness.Actions.ExecuteAsync("update-light", Parameters(
			("lights", new[] { "1", "2" }), ("power", "on"), ("brightness", 50), ("color", "#FF0000"), ("transitionTime", 1000)));

		Assert.That(outcome.Succeeded, Is.True);
		Assert.That(bridge.Writes.Select(write => write.Path), Is.EquivalentTo(new[]
		{
			"/api/valid-key/lights/1/state",
			"/api/valid-key/lights/2/state",
		}));

		var body = bridge.Writes[0].Body!;
		Assert.That(body["on"]!.GetValue<bool>(), Is.True);
		Assert.That(body["bri"]!.GetValue<int>(), Is.EqualTo(127));
		Assert.That(body["transitiontime"]!.GetValue<int>(), Is.EqualTo(10));
		Assert.That(body["xy"]![0]!.GetValue<double>(), Is.EqualTo(0.7006).Within(0.001));
		Assert.That(body["xy"]![1]!.GetValue<double>(), Is.EqualTo(0.2993).Within(0.001));
	}

	[Test]
	public async Task Switching_lights_off_ignores_the_hidden_brightness_and_color()
	{
		var bridge = new FakeHueBridge();
		await using var harness = await Harness.ConfiguredAsync(bridge);

		await harness.Actions.ExecuteAsync("update-light", Parameters(
			("lights", new[] { "1" }), ("power", "off"), ("brightness", 50), ("color", "#FF0000")));

		var body = bridge.Writes.Single().Body!.AsObject();
		Assert.That(body.Select(pair => pair.Key), Is.EquivalentTo(new[] { "on", "transitiontime" }));
		Assert.That(body["on"]!.GetValue<bool>(), Is.False);
	}

	[Test]
	public async Task An_unreadable_color_fails_without_touching_the_bridge()
	{
		var bridge = new FakeHueBridge();
		await using var harness = await Harness.ConfiguredAsync(bridge);

		var outcome = await harness.Actions.ExecuteAsync("update-light", Parameters(
			("lights", new[] { "1" }), ("color", "not a color")));

		Assert.That(outcome.Succeeded, Is.False);
		Assert.That(bridge.Writes, Is.Empty);
	}

	[Test]
	public async Task Update_light_without_lights_is_refused()
	{
		var bridge = new FakeHueBridge();
		await using var harness = await Harness.ConfiguredAsync(bridge);

		var outcome = await harness.Actions.ExecuteAsync("update-light", Parameters(("power", "on")));

		Assert.That(outcome.Succeeded, Is.False);
		Assert.That(bridge.Writes, Is.Empty);
	}

	[Test]
	public async Task A_white_bulb_ignoring_the_color_is_still_a_success()
	{
		var bridge = new FakeHueBridge();
		bridge.LightErrors["2"] = 6;
		await using var harness = await Harness.ConfiguredAsync(bridge);

		var outcome = await harness.Actions.ExecuteAsync("update-light", Parameters(
			("lights", new[] { "1", "2" }), ("color", "#00FF00")));

		Assert.That(outcome.Succeeded, Is.True);
	}

	[Test]
	public async Task A_light_that_is_off_and_cannot_change_is_reported()
	{
		var bridge = new FakeHueBridge();
		bridge.LightErrors["1"] = 201;
		await using var harness = await Harness.ConfiguredAsync(bridge);

		var outcome = await harness.Actions.ExecuteAsync("adjust-light", Parameters(
			("lights", new[] { "1" }), ("brightnessChange", 10)));

		Assert.That(outcome.Succeeded, Is.False);
		Assert.That(outcome.Error!.Message, Does.Contain("off"));
	}

	[Test]
	public async Task Adjust_light_sends_the_macro_deck_2_scaled_increments()
	{
		var bridge = new FakeHueBridge();
		await using var harness = await Harness.ConfiguredAsync(bridge);

		var outcome = await harness.Actions.ExecuteAsync("adjust-light", Parameters(
			("lights", new[] { "1" }), ("brightnessChange", 10), ("saturationChange", -20), ("hueChange", 5), ("colorTemperatureChange", 0)));

		Assert.That(outcome.Succeeded, Is.True);
		var body = bridge.Writes.Single().Body!.AsObject();
		Assert.That(body["bri_inc"]!.GetValue<int>(), Is.EqualTo(25));
		Assert.That(body["sat_inc"]!.GetValue<int>(), Is.EqualTo(-50));
		Assert.That(body["hue_inc"]!.GetValue<int>(), Is.EqualTo(3276));
		Assert.That(body.ContainsKey("ct_inc"), Is.False);
	}

	[Test]
	public async Task Adjust_light_with_every_change_at_zero_is_a_no_op()
	{
		var bridge = new FakeHueBridge();
		await using var harness = await Harness.ConfiguredAsync(bridge);

		var outcome = await harness.Actions.ExecuteAsync("adjust-light", Parameters(("lights", new[] { "1" })));

		Assert.That(outcome.Succeeded, Is.True);
		Assert.That(bridge.Writes, Is.Empty);
	}

	[Test]
	public async Task With_two_bridges_an_action_must_name_one()
	{
		var bridge = new FakeHueBridge();
		await using var harness = Harness.Create(bridge);
		Harness.Seed(harness, FakeHueBridge.BridgeId, FakeHueBridge.AppKey, FakeHueBridge.Host, "First");
		Harness.Seed(harness, "001788fffe999999", "other-key", "192.168.1.30", "Second");
		await harness.InitializeIntegrationsAsync();

		var unnamed = await harness.Actions.ExecuteAsync("set-scene", Parameters(("scene", "abc")));
		var named = await harness.Actions.ExecuteAsync("set-scene", Parameters(("scene", "abc"), ("bridge", FakeHueBridge.BridgeId)));

		Assert.That(unnamed.Succeeded, Is.False);
		Assert.That(unnamed.Error!.Message, Does.Contain("Choose a bridge"));
		Assert.That(named.Succeeded, Is.True);
	}

	[Test]
	public async Task The_light_list_comes_from_the_bridge_sorted_by_name()
	{
		await using var harness = await Harness.ConfiguredAsync(new FakeHueBridge());

		var options = (await harness.Actions.GetOptionsAsync("update-light", "lights")).DataAs<DynamicOptionsResultDto>();

		Assert.That(options!.Options.Select(option => option.Value), Is.EqualTo(new[] { "2", "1" }));
		Assert.That(options.Options.Select(option => option.Label!.Value.Literal), Is.EqualTo(new[] { "Ceiling", "Desk" }));
	}

	[Test]
	public async Task Scenes_are_labelled_with_their_room()
	{
		await using var harness = await Harness.ConfiguredAsync(new FakeHueBridge());

		var options = (await harness.Actions.GetOptionsAsync("set-scene", "scene")).DataAs<DynamicOptionsResultDto>();

		Assert.That(options!.Options.Select(option => option.Label!.Value.Literal), Is.EqualTo(new[] { "Bright", "Relax (Office)" }));
	}

	[Test]
	public async Task An_unreachable_bridge_explains_the_empty_light_list()
	{
		var bridge = new FakeHueBridge { CurrentHost = "192.168.1.99", DiscoveryAvailable = false };
		await using var harness = await Harness.ConfiguredAsync(bridge);

		var options = (await harness.Actions.GetOptionsAsync("update-light", "lights")).DataAs<DynamicOptionsResultDto>();

		Assert.That(options!.Options, Is.Empty);
		Assert.That(options.Error, Is.Not.Null);
	}

	private static Dictionary<string, object?> Parameters(params (string Name, object? Value)[] values)
		=> values.ToDictionary(value => value.Name, value => value.Value);
}
