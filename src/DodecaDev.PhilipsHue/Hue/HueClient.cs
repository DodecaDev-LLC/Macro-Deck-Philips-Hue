using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DodecaDev.PhilipsHue.Hue;

/// <summary>
/// The local Hue v1 REST API, stateless: every call names the bridge it talks to, so one instance serves
/// every configured bridge and the setup flow can talk to a bridge before it is configured.
/// </summary>
/// <remarks>
/// The v1 API answers most failures with HTTP 200 and an error array, so the status code alone says little.
/// Every response goes through <see cref="ThrowOnErrorArray"/> or is returned as per-item errors.
/// </remarks>
public sealed class HueClient(IHttpClientFactory httpClientFactory)
{
	public const string HttpClientName = "hue";

	public async Task<HueBridgeInfo> GetBridgeInfoAsync(string host, CancellationToken cancellationToken)
	{
		var node = await GetAsync(host, "api/config", cancellationToken);
		var bridgeId = node["bridgeid"]?.GetValue<string>();
		if (string.IsNullOrWhiteSpace(bridgeId))
		{
			throw HueException.InvalidResponse("no bridgeid in /api/config");
		}

		var name = node["name"]?.GetValue<string>();
		return new HueBridgeInfo(NormalizeBridgeId(bridgeId), string.IsNullOrWhiteSpace(name) ? bridgeId : name);
	}

	/// <summary>Creates an app key. Fails with <see cref="HueFailure.LinkButtonNotPressed"/> until the user
	/// has pressed the bridge's link button within the last 30 seconds.</summary>
	public async Task<string> RegisterAsync(string host, string deviceType, CancellationToken cancellationToken)
	{
		var body = new JsonObject { ["devicetype"] = deviceType };
		var node = await SendAsync(host, HttpMethod.Post, "api", body, cancellationToken);

		var errors = ReadErrors(node);
		if (errors.Any(error => error.Type == HueApiError.LinkButtonNotPressed))
		{
			throw HueException.LinkButtonNotPressed();
		}

		if (errors.Count > 0)
		{
			throw HueException.Rejected(errors[0].Type, errors[0].Description);
		}

		var username = node is JsonArray array
			? array.Select(item => item?["success"]?["username"]?.GetValue<string>()).FirstOrDefault(value => value is not null)
			: null;

		return username ?? throw HueException.InvalidResponse("no username in the registration answer");
	}

	/// <summary>A cheap authenticated read that tells a revoked app key apart from an unreachable bridge.</summary>
	public Task VerifyAsync(string host, string appKey, CancellationToken cancellationToken)
		=> GetAsync(host, $"api/{Escape(appKey)}/groups/0", cancellationToken);

	public async Task<IReadOnlyList<HueLight>> GetLightsAsync(string host, string appKey, CancellationToken cancellationToken)
	{
		var node = await GetAsync(host, $"api/{Escape(appKey)}/lights", cancellationToken);
		return [.. Entries(node).Select(entry => new HueLight(entry.Id, NameOf(entry.Value, entry.Id)))];
	}

	public async Task<IReadOnlyList<HueGroup>> GetGroupsAsync(string host, string appKey, CancellationToken cancellationToken)
	{
		var node = await GetAsync(host, $"api/{Escape(appKey)}/groups", cancellationToken);
		return [.. Entries(node).Select(entry => new HueGroup(entry.Id, NameOf(entry.Value, entry.Id)))];
	}

	public async Task<IReadOnlyList<HueScene>> GetScenesAsync(string host, string appKey, CancellationToken cancellationToken)
	{
		var node = await GetAsync(host, $"api/{Escape(appKey)}/scenes", cancellationToken);
		return [.. Entries(node).Select(entry => new HueScene(
			entry.Id,
			NameOf(entry.Value, entry.Id),
			entry.Value["group"]?.GetValue<string>()))];
	}

	/// <summary>Applies a state change. Per-parameter failures come back as errors rather than an exception,
	/// because a bridge applies the parameters it can and refuses the rest.</summary>
	public async Task<IReadOnlyList<HueApiError>> SetLightStateAsync(
		string host, string appKey, string lightId, HueLightState state, CancellationToken cancellationToken)
	{
		var node = await SendAsync(host, HttpMethod.Put, $"api/{Escape(appKey)}/lights/{Escape(lightId)}/state",
			ToJson(state), cancellationToken);
		return ThrowOnUnauthorized(ReadErrors(node));
	}

	public async Task<IReadOnlyList<HueApiError>> RecallSceneAsync(
		string host, string appKey, string groupId, string sceneId, CancellationToken cancellationToken)
	{
		var body = new JsonObject { ["scene"] = sceneId };
		var node = await SendAsync(host, HttpMethod.Put, $"api/{Escape(appKey)}/groups/{Escape(groupId)}/action",
			body, cancellationToken);
		return ThrowOnUnauthorized(ReadErrors(node));
	}

	/// <summary>Bridge ids differ in case between the discovery service and <c>/api/config</c>.</summary>
	public static string NormalizeBridgeId(string bridgeId) => bridgeId.Trim().ToLowerInvariant();

	internal static JsonObject ToJson(HueLightState state)
	{
		var body = new JsonObject();
		if (state.On is { } on)
		{
			body["on"] = on;
		}

		if (state.Brightness is { } brightness)
		{
			body["bri"] = Math.Clamp(brightness, 1, 254);
		}

		if (state.Xy is { } xy)
		{
			body["xy"] = new JsonArray(Math.Round(xy.X, 4), Math.Round(xy.Y, 4));
		}

		if (state.TransitionTime is { } transitionTime)
		{
			body["transitiontime"] = Math.Clamp(transitionTime, 0, ushort.MaxValue);
		}

		if (state.BrightnessIncrement is { } brightnessIncrement)
		{
			body["bri_inc"] = Math.Clamp(brightnessIncrement, -254, 254);
		}

		if (state.SaturationIncrement is { } saturationIncrement)
		{
			body["sat_inc"] = Math.Clamp(saturationIncrement, -254, 254);
		}

		if (state.HueIncrement is { } hueIncrement)
		{
			body["hue_inc"] = Math.Clamp(hueIncrement, -65534, 65534);
		}

		if (state.ColorTemperatureIncrement is { } colorTemperatureIncrement)
		{
			body["ct_inc"] = Math.Clamp(colorTemperatureIncrement, -65534, 65534);
		}

		return body;
	}

	private async Task<JsonNode> GetAsync(string host, string path, CancellationToken cancellationToken)
	{
		var node = await SendAsync(host, HttpMethod.Get, path, body: null, cancellationToken);
		ThrowOnErrorArray(node);
		return node;
	}

	private async Task<JsonNode> SendAsync(
		string host, HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken)
	{
		using var request = new HttpRequestMessage(method, BuildUri(host, path));
		if (body is not null)
		{
			request.Content = JsonContent.Create(body);
		}

		var client = httpClientFactory.CreateClient(HttpClientName);
		HttpResponseMessage response;
		try
		{
			response = await client.SendAsync(request, cancellationToken);
		}
		catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
		{
			// Cancelled without the caller asking for it: the HttpClient's own timeout.
			throw HueException.Timeout(host, exception);
		}
		catch (HttpRequestException exception)
		{
			throw HueException.Unreachable(host, exception);
		}

		using (response)
		{
			if (!response.IsSuccessStatusCode)
			{
				throw HueException.InvalidResponse(
					$"HTTP {((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)} for {path}");
			}

			try
			{
				var text = await response.Content.ReadAsStringAsync(cancellationToken);
				return JsonNode.Parse(text) ?? throw HueException.InvalidResponse($"empty body for {path}");
			}
			catch (JsonException exception)
			{
				throw HueException.InvalidResponse($"malformed JSON for {path}", exception);
			}
		}
	}

	private static Uri BuildUri(string host, string path)
		=> new UriBuilder(Uri.UriSchemeHttp, host) { Path = path }.Uri;

	private static void ThrowOnErrorArray(JsonNode node)
	{
		var errors = ReadErrors(node);
		ThrowOnUnauthorized(errors);
		if (errors.Count > 0)
		{
			throw HueException.Rejected(errors[0].Type, errors[0].Description);
		}
	}

	private static IReadOnlyList<HueApiError> ThrowOnUnauthorized(IReadOnlyList<HueApiError> errors)
		=> errors.Any(error => error.Type == HueApiError.UnauthorizedUser) ? throw HueException.Unauthorized() : errors;

	private static List<HueApiError> ReadErrors(JsonNode node)
	{
		var errors = new List<HueApiError>();
		if (node is not JsonArray array)
		{
			return errors;
		}

		foreach (var item in array)
		{
			if (item?["error"] is JsonObject error)
			{
				errors.Add(new HueApiError(
					error["type"]?.GetValue<int>() ?? 0,
					error["description"]?.GetValue<string>() ?? string.Empty));
			}
		}

		return errors;
	}

	private static IEnumerable<(string Id, JsonNode Value)> Entries(JsonNode node)
		=> node is JsonObject map
			? map.Where(pair => pair.Value is not null).Select(pair => (pair.Key, pair.Value!))
			: throw HueException.InvalidResponse("expected an object keyed by id");

	private static string NameOf(JsonNode value, string fallback)
		=> value["name"]?.GetValue<string>() is { Length: > 0 } name ? name : fallback;

	private static string Escape(string segment) => Uri.EscapeDataString(segment);
}
