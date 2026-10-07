namespace DodecaDev.PhilipsHue.Hue;

/// <summary>What an unauthenticated <c>GET /api/config</c> tells about a bridge.</summary>
public sealed record HueBridgeInfo(string BridgeId, string Name);

/// <summary>A bridge found on the network, either through the discovery service or typed in by the user.</summary>
public sealed record DiscoveredBridge(string BridgeId, string Host);

public sealed record HueLight(string Id, string Name);

public sealed record HueGroup(string Id, string Name);

public sealed record HueScene(string Id, string Name, string? GroupId);

/// <summary>One entry of the error array the v1 API answers a write with.</summary>
public sealed record HueApiError(int Type, string Description)
{
	public const int UnauthorizedUser = 1;
	public const int ResourceNotAvailable = 3;
	public const int ParameterNotAvailable = 6;
	public const int LinkButtonNotPressed = 101;
	public const int DeviceIsOff = 201;
}

/// <summary>
/// A state change for one light in v1 terms. Null members are left out of the request, so the light keeps
/// whatever it had.
/// </summary>
public sealed record HueLightState
{
	public bool? On { get; init; }

	/// <summary>1-254.</summary>
	public int? Brightness { get; init; }

	public (double X, double Y)? Xy { get; init; }

	/// <summary>In multiples of 100 ms, as the bridge expects.</summary>
	public int? TransitionTime { get; init; }

	public int? BrightnessIncrement { get; init; }

	public int? SaturationIncrement { get; init; }

	public int? HueIncrement { get; init; }

	public int? ColorTemperatureIncrement { get; init; }

	public bool IsEmpty => this == new HueLightState();
}
