using MacroDeck.Localization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;

namespace DodecaDev.PhilipsHue.Actions;

/// <summary>Recalls a scene stored on the bridge.</summary>
public sealed class SetSceneAction(HueActionSupport support) : IActionDefinition, IDynamicOptionsActionDefinition
{
	public const string ActionId = "set-scene";
	public const string SceneParameter = "scene";
	public const string RoomParameter = "room";

	/// <summary>Group 0 always holds every light, so recalling there applies the scene to its own lights.</summary>
	private const string AllLightsGroup = "0";

	public string Id => ActionId;

	public LocalizedText Name => Strings.Actions.SetScene.Name();

	public LocalizedText Description => Strings.Actions.SetScene.Description();

	public IReadOnlyList<ActionParameter> Parameters { get; } =
	[
		HueActionSupport.BridgeField(),
		ActionParameter.DynamicChoice(
			SceneParameter,
			label: Strings.Actions.SetScene.Scene.Label(),
			required: true),
		ActionParameter.DynamicChoice(
			RoomParameter,
			label: Strings.Actions.SetScene.Room.Label(),
			description: Strings.Actions.SetScene.Room.Description(),
			placeholder: Strings.Actions.SetScene.Room.Placeholder()),
	];

	public MacroDeckPlatform Platforms => MacroDeckPlatform.All;

	public IActionExecutor CreateExecutor() => new Executor(support);

	public async Task<DynamicOptionsResult> GetDynamicOptionsAsync(DynamicOptionsContext context, CancellationToken cancellationToken)
	{
		switch (context.ParameterName)
		{
			case HueActionSupport.BridgeParameter:
				return support.BridgeOptions(context.Filter);

			case SceneParameter:
				return await support.OptionsFromBridgeAsync(context, async (bridge, host, ct) =>
				{
					var scenes = await support.Client.GetScenesAsync(host, bridge.AppKey, ct);
					var groups = (await support.Client.GetGroupsAsync(host, bridge.AppKey, ct)).ToDictionary(group => group.Id, group => group.Name);
					return scenes.Select(scene => (scene.Id,
						scene.GroupId is { } groupId && groups.TryGetValue(groupId, out var room) ? $"{scene.Name} ({room})" : scene.Name));
				}, cancellationToken);

			case RoomParameter:
				return await support.OptionsFromBridgeAsync(context, async (bridge, host, ct) =>
					(await support.Client.GetGroupsAsync(host, bridge.AppKey, ct)).Select(group => (group.Id, group.Name)),
					cancellationToken);

			default:
				return new DynamicOptionsResult { Options = [] };
		}
	}

	private sealed class Executor(HueActionSupport support) : IActionExecutor
	{
		public Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
		{
			if (ParameterValues.String(context.Parameters, SceneParameter) is not { } sceneId)
			{
				return Task.FromResult(ActionResult.Failed(ActionErrorCodes.InvalidParameter,
					MacroDeckStrings.Validation.Required(Strings.Actions.SetScene.Scene.Label())));
			}

			var groupId = ParameterValues.String(context.Parameters, RoomParameter) ?? AllLightsGroup;

			return support.RunAsync(context.Parameters, async (bridge, host) =>
			{
				var errors = await support.Client.RecallSceneAsync(host, bridge.AppKey, groupId, sceneId, context.CancellationToken);
				return HueActionSupport.FromWriteErrors(errors);
			});
		}
	}
}
