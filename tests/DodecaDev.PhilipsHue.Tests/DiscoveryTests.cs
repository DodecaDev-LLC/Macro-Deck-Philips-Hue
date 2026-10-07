using System.Net;
using System.Text;
using DodecaDev.PhilipsHue.Hue;
using MacroDeck.Plugin.Protocol.Capabilities.ConfigFlow;
using NUnit.Framework;

namespace DodecaDev.PhilipsHue.Tests;

[TestFixture]
public sealed class DiscoveryTests
{
	[Test]
	public async Task A_bridge_found_on_mdns_is_offered_without_asking_the_cloud_service()
	{
		var bridge = new FakeHueBridge();
		var mdns = new FakeMdnsBrowser { Answers = { new MdnsBridge(FakeHueBridge.Host, FakeHueBridge.BridgeId) } };
		await using var harness = Harness.Create(bridge, mdns);
		await harness.InitializeIntegrationsAsync();

		var result = (await harness.ConfigFlow.StartAsync(new FlowStartArguments
		{
			SessionId = "s",
			OAuth = new ConfigFlowOAuthContextDto { RedirectUri = "http://localhost/callback", State = "x" },
		})).DataAs<ConfigFlowResultDto>();

		var options = result!.NextStep!.Fields.Single(field => field.Name == "bridgeAddress").Options!;
		Assert.That(options.Select(option => option.Value), Is.EqualTo(new[] { FakeHueBridge.Host, "manual" }));
		Assert.That(bridge.Requests.Any(request => request.Host == "discovery.meethue.com"), Is.False);
	}

	[Test]
	public async Task A_migrated_bridge_is_located_by_mdns_when_the_cloud_service_is_rate_limited()
	{
		var bridge = new FakeHueBridge { DiscoveryAvailable = false };
		var mdns = new FakeMdnsBrowser { Answers = { new MdnsBridge(FakeHueBridge.Host, null) } };
		await using var harness = Harness.Create(bridge, mdns);
		Harness.Seed(harness, FakeHueBridge.BridgeId, FakeHueBridge.AppKey, host: null, FakeHueBridge.Name);
		await harness.InitializeIntegrationsAsync();

		await Harness.UntilAsync(async () =>
			await harness.Context.Config.GetStringAsync(harness.Context.Config.Entries[0].Id, "host") == FakeHueBridge.Host,
			because: "an mDNS answer without an id should be resolved through the bridge's own config");
	}

	[Test]
	public async Task When_mdns_finds_nothing_the_subnet_scan_is_used_before_the_cloud_service()
	{
		var bridge = new FakeHueBridge();
		var scanner = new FakeSubnetScanner { Found = { new DiscoveredBridge(FakeHueBridge.BridgeId, FakeHueBridge.Host) } };
		await using var harness = Harness.Create(bridge, scanner: scanner);
		await harness.InitializeIntegrationsAsync();

		var result = (await harness.ConfigFlow.StartAsync(new FlowStartArguments
		{
			SessionId = "s",
			OAuth = new ConfigFlowOAuthContextDto { RedirectUri = "http://localhost/callback", State = "x" },
		})).DataAs<ConfigFlowResultDto>();

		Assert.That(result!.NextStep!.Fields.Single(field => field.Name == "bridgeAddress").Options![0].Value, Is.EqualTo(FakeHueBridge.Host));
		Assert.That(bridge.Requests.Any(request => request.Host == "discovery.meethue.com"), Is.False);
	}

	[TestCase("192.168.50.32", 24, 254, "192.168.50.1", "192.168.50.254")]
	[TestCase("10.0.0.5", 30, 2, "10.0.0.5", "10.0.0.6")]
	[TestCase("10.0.0.5", 16, 0, null, null)]
	public void A_subnet_yields_its_host_addresses(string address, int prefix, int count, string? first, string? last)
	{
		var hosts = SubnetScanner.HostsAround(IPAddress.Parse(address), prefix).ToList();

		Assert.That(hosts, Has.Count.EqualTo(count));
		if (count > 0)
		{
			Assert.That(hosts[0].ToString(), Is.EqualTo(first));
			Assert.That(hosts[^1].ToString(), Is.EqualTo(last));
		}
	}

	[Test]
	public void The_query_asks_for_the_hue_service_with_a_unicast_response()
	{
		var query = MdnsMessage.BuildQuery(MdnsBrowser.ServiceName);

		Assert.That(query[5], Is.EqualTo(1), "one question");
		Assert.That(Encoding.ASCII.GetString(query, 13, 4), Is.EqualTo("_hue"));
		Assert.That(query[^4..], Is.EqualTo(new byte[] { 0, 12, 0x80, 1 }), "PTR, class IN with the QU bit");
	}

	[Test]
	public void A_bridge_answer_yields_its_address_and_id()
	{
		var packet = Response(
			Record(Name("_hue._tcp.local"), type: 12, Name("Hue Bridge - 123456._hue._tcp.local")),
			Record(Name("Hue Bridge - 123456._hue._tcp.local"), type: 16, Txt("bridgeid=001788FFFE123456", "modelid=BSB002")),
			Record(Name("ecb5fa123456.local"), type: 1, [192, 168, 1, 20]));

		Assert.That(MdnsMessage.TryReadHueAnswer(packet, IPAddress.Parse("10.9.9.9"), out var bridge), Is.True);
		Assert.That(bridge.Host, Is.EqualTo("192.168.1.20"));
		Assert.That(bridge.BridgeId, Is.EqualTo("001788fffe123456"));
	}

	[Test]
	public void Compressed_names_are_followed()
	{
		// The PTR record's owner name points back at the question's name (offset 12).
		var header = new byte[] { 0, 0, 0x84, 0, 0, 1, 0, 1, 0, 0, 0, 0 };
		var question = Name("_hue._tcp.local").Concat(new byte[] { 0, 12, 0, 1 });
		var answer = Record([0xC0, 12], type: 12, Name("x._hue._tcp.local"));
		var packet = header.Concat(question).Concat(answer).ToArray();

		Assert.That(MdnsMessage.TryReadHueAnswer(packet, IPAddress.Parse("192.168.1.21"), out var bridge), Is.True);
		Assert.That(bridge.Host, Is.EqualTo("192.168.1.21"), "no A record: the sender's address");
		Assert.That(bridge.BridgeId, Is.Null);
	}

	[Test]
	public void Answers_for_other_services_and_garbage_are_ignored()
	{
		var printer = Response(Record(Name("_ipp._tcp.local"), type: 12, Name("Printer._ipp._tcp.local")));

		Assert.That(MdnsMessage.TryReadHueAnswer(printer, IPAddress.Loopback, out _), Is.False);
		Assert.That(MdnsMessage.TryReadHueAnswer([0, 0, 0x84, 0, 0, 0, 0, 5, 0, 0, 0, 0, 3], IPAddress.Loopback, out _), Is.False);
		Assert.That(MdnsMessage.TryReadHueAnswer([1, 2, 3], IPAddress.Loopback, out _), Is.False);
	}

	private static byte[] Response(params byte[][] records)
	{
		var header = new byte[] { 0, 0, 0x84, 0, 0, 0, 0, (byte)records.Length, 0, 0, 0, 0 };
		return [.. header, .. records.SelectMany(record => record)];
	}

	private static byte[] Record(byte[] name, ushort type, byte[] data)
		=> [.. name, (byte)(type >> 8), (byte)type, 0x80, 1, 0, 0, 0x11, 0x94, (byte)(data.Length >> 8), (byte)data.Length, .. data];

	private static byte[] Name(string name)
		=> [.. name.Split('.').SelectMany(label => new[] { (byte)label.Length }.Concat(Encoding.UTF8.GetBytes(label))), 0];

	private static byte[] Txt(params string[] entries)
		=> [.. entries.SelectMany(entry => new[] { (byte)entry.Length }.Concat(Encoding.UTF8.GetBytes(entry)))];
}
