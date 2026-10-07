using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace DodecaDev.PhilipsHue.Tests;

/// <summary>
/// A Hue bridge and the discovery service behind one HTTP handler. The plugin still builds real requests and
/// parses real v1 responses, including the HTTP 200 error arrays the bridge answers failures with.
/// </summary>
internal sealed class FakeHueBridge : HttpMessageHandler
{
	internal const string BridgeId = "001788fffe123456";
	internal const string Host = "192.168.1.20";
	internal const string AppKey = "valid-key";
	internal const string Name = "Living Room Bridge";

	private readonly Lock _gate = new();
	private readonly List<RecordedRequest> _requests = [];

	/// <summary>The address the bridge actually answers on; anything else is unreachable.</summary>
	internal string CurrentHost { get; set; } = Host;

	internal bool LinkButtonPressed { get; set; }

	internal bool DiscoveryAvailable { get; set; } = true;

	/// <summary>Error type to answer every light-state write with, per light id.</summary>
	internal Dictionary<string, int> LightErrors { get; } = [];

	internal IReadOnlyList<RecordedRequest> Requests
	{
		get
		{
			lock (_gate)
			{
				return [.. _requests];
			}
		}
	}

	internal IReadOnlyList<RecordedRequest> Writes => [.. Requests.Where(request => request.Method != HttpMethod.Get.Method)];

	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		var uri = request.RequestUri!;
		var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
		lock (_gate)
		{
			_requests.Add(new RecordedRequest(request.Method.Method, uri.Host, uri.AbsolutePath, body is null ? null : JsonNode.Parse(body)));
		}

		if (uri.Host == "discovery.meethue.com")
		{
			return DiscoveryAvailable
				? Json(new JsonArray(new JsonObject { ["id"] = BridgeId, ["internalipaddress"] = CurrentHost, ["port"] = 443 }))
				: new HttpResponseMessage(HttpStatusCode.TooManyRequests);
		}

		if (uri.Host != CurrentHost)
		{
			throw new HttpRequestException("No route to host.");
		}

		var segments = uri.AbsolutePath.Trim('/').Split('/');
		if (segments is ["api"] && request.Method == HttpMethod.Post)
		{
			return LinkButtonPressed
				? Json(new JsonArray(new JsonObject { ["success"] = new JsonObject { ["username"] = AppKey } }))
				: Error(101, "", "link button not pressed");
		}

		if (segments is ["api", "config"])
		{
			return Json(new JsonObject { ["name"] = Name, ["bridgeid"] = BridgeId.ToUpperInvariant() });
		}

		if (segments.Length < 3 || segments[1] != AppKey)
		{
			return Error(1, "/" + string.Join('/', segments.Skip(2)), "unauthorized user");
		}

		return segments[2..] switch
		{
			["lights"] => Json(new JsonObject
			{
				["1"] = new JsonObject { ["name"] = "Desk" },
				["2"] = new JsonObject { ["name"] = "Ceiling" },
			}),
			["groups"] => Json(new JsonObject
			{
				["1"] = new JsonObject { ["name"] = "Office", ["type"] = "Room" },
			}),
			["groups", "0"] => Json(new JsonObject { ["name"] = "Group 0", ["lights"] = new JsonArray("1", "2") }),
			["scenes"] => Json(new JsonObject
			{
				["abc"] = new JsonObject { ["name"] = "Relax", ["type"] = "GroupScene", ["group"] = "1" },
				["xyz"] = new JsonObject { ["name"] = "Bright", ["type"] = "LightScene" },
			}),
			["lights", var lightId, "state"] => LightWrite(lightId),
			["groups", var groupId, "action"] => Json(new JsonArray(new JsonObject
			{
				["success"] = new JsonObject { [$"/groups/{groupId}/action/scene"] = "ok" },
			})),
			_ => Error(3, uri.AbsolutePath, "resource not available"),
		};
	}

	private HttpResponseMessage LightWrite(string lightId)
		=> LightErrors.TryGetValue(lightId, out var errorType)
			? Error(errorType, $"/lights/{lightId}/state/xy", "refused")
			: Json(new JsonArray(new JsonObject { ["success"] = new JsonObject { [$"/lights/{lightId}/state/on"] = true } }));

	private static HttpResponseMessage Error(int type, string address, string description)
		=> Json(new JsonArray(new JsonObject
		{
			["error"] = new JsonObject { ["type"] = type, ["address"] = address, ["description"] = description },
		}));

	private static HttpResponseMessage Json(JsonNode node)
		=> new(HttpStatusCode.OK) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };

	internal sealed record RecordedRequest(string Method, string Host, string Path, JsonNode? Body);
}
