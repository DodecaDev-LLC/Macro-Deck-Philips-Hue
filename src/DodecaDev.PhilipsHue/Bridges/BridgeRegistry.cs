using DodecaDev.PhilipsHue.Hue;

namespace DodecaDev.PhilipsHue.Bridges;

public enum BridgeStatus
{
	Unknown,
	Online,
	Unreachable,
	Unauthorized,
}

/// <summary>One config entry, as the integration last read it plus what probing found out since.</summary>
public sealed record ConfiguredBridge(
	Guid EntryId,
	string BridgeId,
	string Title,
	string? Host,
	string AppKey,
	BridgeStatus Status = BridgeStatus.Unknown);

/// <summary>
/// The bridges every action, option list and issue reads from. Invocations run concurrently, so the set is
/// an immutable snapshot swapped under a lock; readers never see a half-applied change.
/// </summary>
public sealed class BridgeRegistry
{
	private readonly Lock _gate = new();
	private IReadOnlyList<ConfiguredBridge> _bridges = [];

	public IReadOnlyList<ConfiguredBridge> All => Volatile.Read(ref _bridges);

	public void Replace(IEnumerable<ConfiguredBridge> bridges)
	{
		var snapshot = bridges.ToList();
		lock (_gate)
		{
			Volatile.Write(ref _bridges, snapshot);
		}
	}

	public ConfiguredBridge? Find(string bridgeId)
		=> All.FirstOrDefault(bridge => string.Equals(bridge.BridgeId, bridgeId, StringComparison.OrdinalIgnoreCase));

	/// <summary>Applies a probe result unless the entry was removed or replaced in the meantime.</summary>
	public void Update(ConfiguredBridge updated)
	{
		lock (_gate)
		{
			var next = _bridges.ToList();
			var index = next.FindIndex(bridge => bridge.EntryId == updated.EntryId);
			if (index < 0 || next[index].AppKey != updated.AppKey)
			{
				return;
			}

			next[index] = updated;
			Volatile.Write(ref _bridges, next);
		}
	}

	/// <summary>The bridge an action means: the one it names, or the only one there is when it names none.</summary>
	public BridgeLookup Resolve(string? requestedBridgeId)
	{
		var bridges = All;
		if (bridges.Count == 0)
		{
			return BridgeLookup.Failed(BridgeLookupFailure.NoneConfigured);
		}

		if (string.IsNullOrWhiteSpace(requestedBridgeId))
		{
			return bridges.Count == 1
				? BridgeLookup.Found(bridges[0])
				: BridgeLookup.Failed(BridgeLookupFailure.Ambiguous);
		}

		return Find(HueClient.NormalizeBridgeId(requestedBridgeId)) is { } bridge
			? BridgeLookup.Found(bridge)
			: BridgeLookup.Failed(BridgeLookupFailure.Unknown);
	}
}

public enum BridgeLookupFailure
{
	None,
	NoneConfigured,
	Ambiguous,
	Unknown,
}

public readonly record struct BridgeLookup(ConfiguredBridge? Bridge, BridgeLookupFailure Failure)
{
	public static BridgeLookup Found(ConfiguredBridge bridge) => new(bridge, BridgeLookupFailure.None);

	public static BridgeLookup Failed(BridgeLookupFailure failure) => new(null, failure);
}
