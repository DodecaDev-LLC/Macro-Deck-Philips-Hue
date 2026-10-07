using System.Globalization;

namespace DodecaDev.PhilipsHue.Hue;

/// <summary>
/// sRGB to CIE xy, the color space Hue bulbs take. The bridge clamps xy into each bulb's own gamut, so no
/// per-model gamut correction happens here.
/// </summary>
public static class HueColor
{
	private static readonly (double X, double Y) _whitePoint = (0.3227, 0.3290);

	/// <summary>Parses <c>#RGB</c>, <c>#RRGGBB</c> or <c>#RRGGBBAA</c> (alpha ignored).</summary>
	public static bool TryParseHex(string? text, out (byte R, byte G, byte B) rgb)
	{
		rgb = default;
		var hex = text?.Trim().TrimStart('#');
		if (hex is null)
		{
			return false;
		}

		if (hex.Length == 3)
		{
			hex = string.Concat(hex.Select(c => new string(c, 2)));
		}

		if (hex.Length is not (6 or 8) ||
			!uint.TryParse(hex.AsSpan(0, 6), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
		{
			return false;
		}

		rgb = ((byte)(value >> 16), (byte)(value >> 8), (byte)value);
		return true;
	}

	public static string ToHex(byte r, byte g, byte b)
		=> string.Create(CultureInfo.InvariantCulture, $"#{r:X2}{g:X2}{b:X2}");

	public static (double X, double Y) ToXy(byte r, byte g, byte b)
	{
		var red = Linearize(r);
		var green = Linearize(g);
		var blue = Linearize(b);

		// Wide gamut D65 conversion from Philips' "RGB to xy color conversion" developer note.
		var x = red * 0.664511 + green * 0.154324 + blue * 0.162028;
		var y = red * 0.283881 + green * 0.668433 + blue * 0.047685;
		var z = red * 0.000088 + green * 0.072310 + blue * 0.986039;

		var sum = x + y + z;
		return sum <= 0 ? _whitePoint : (x / sum, y / sum);
	}

	private static double Linearize(byte channel)
	{
		var value = channel / 255.0;
		return value > 0.04045 ? Math.Pow((value + 0.055) / 1.055, 2.4) : value / 12.92;
	}
}
