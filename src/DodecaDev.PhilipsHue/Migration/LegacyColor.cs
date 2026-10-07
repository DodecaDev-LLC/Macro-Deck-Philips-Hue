using System.Drawing;
using System.Globalization;
using System.Text.Json;
using DodecaDev.PhilipsHue.Hue;

namespace DodecaDev.PhilipsHue.Migration;

/// <summary>
/// Reads a <see cref="Color"/> the way Newtonsoft wrote it for the Macro Deck 2 plugin. Depending on the
/// runtime it went through the type converter ("White", "255, 128, 0", "128, 255, 128, 0") or was written
/// as an object with R, G, B and Name, whose Name is a known color or ARGB hex.
/// </summary>
internal static class LegacyColor
{
	public static bool TryRead(JsonElement element, out string hex)
	{
		hex = string.Empty;
		return element.ValueKind switch
		{
			JsonValueKind.String => TryReadText(element.GetString(), out hex),
			JsonValueKind.Object => TryReadObject(element, out hex),
			_ => false,
		};
	}

	private static bool TryReadObject(JsonElement element, out string hex)
	{
		hex = string.Empty;
		if (TryChannel(element, "R", out var r) && TryChannel(element, "G", out var g) && TryChannel(element, "B", out var b))
		{
			hex = HueColor.ToHex(r, g, b);
			return true;
		}

		return element.TryGetProperty("Name", out var name) && TryReadText(name.GetString(), out hex);
	}

	private static bool TryReadText(string? text, out string hex)
	{
		hex = string.Empty;
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}

		var trimmed = text.Trim();
		var parts = trimmed.Split(',', StringSplitOptions.TrimEntries);
		if (parts.Length is 3 or 4)
		{
			var channels = parts[^3..];
			if (channels.All(part => byte.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
			{
				var rgb = channels.Select(part => byte.Parse(part, CultureInfo.InvariantCulture)).ToArray();
				hex = HueColor.ToHex(rgb[0], rgb[1], rgb[2]);
				return true;
			}

			return false;
		}

		// Color.Name of an unnamed color is its ARGB value in hex, e.g. "ffff8000".
		if (trimmed.Length == 8 && uint.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb))
		{
			hex = HueColor.ToHex((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
			return true;
		}

		if (HueColor.TryParseHex(trimmed, out var parsed) && trimmed.StartsWith('#'))
		{
			hex = HueColor.ToHex(parsed.R, parsed.G, parsed.B);
			return true;
		}

		var known = Color.FromName(trimmed);
		if (known.IsKnownColor)
		{
			hex = HueColor.ToHex(known.R, known.G, known.B);
			return true;
		}

		return false;
	}

	private static bool TryChannel(JsonElement element, string name, out byte value)
	{
		value = 0;
		return element.TryGetProperty(name, out var channel) &&
			channel.ValueKind == JsonValueKind.Number &&
			channel.TryGetByte(out value);
	}
}
