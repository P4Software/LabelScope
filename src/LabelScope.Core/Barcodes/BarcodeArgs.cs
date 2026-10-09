using System.Globalization;

namespace LabelScope.Core.Barcodes;

/// <summary>Typed, forgiving access to the comma separated parameters of one <c>^B?</c> command.</summary>
internal sealed class BarcodeArgs
{
    private readonly string[] _raw;

    /// <summary>Wraps <paramref name="raw"/>, the command arguments split at commas.</summary>
    /// <param name="raw">Parameters in command order; missing ones read as empty.</param>
    /// <param name="by">Current <c>^BY</c> values.</param>
    /// <param name="fieldOrientation">Orientation from <c>^FW</c>, used when parameter 0 is empty.</param>
    public BarcodeArgs(string[] raw, BarDefaults by, char fieldOrientation)
    {
        _raw = raw;
        By = by;
        var o = Letter(0, '\0');
        Orientation = o is 'N' or 'R' or 'I' or 'B' ? o : fieldOrientation;
    }

    /// <summary>The current <c>^BY</c> values.</summary>
    public BarDefaults By { get; }

    /// <summary>Field orientation N, R, I or B (explicit parameter 0, otherwise the <c>^FW</c> default).</summary>
    public char Orientation { get; }

    /// <summary>Parameter <paramref name="i"/> trimmed; empty when absent.</summary>
    public string Text(int i) => i < _raw.Length ? _raw[i].Trim() : "";

    /// <summary>Parameter as a whole number, or <paramref name="fallback"/> when absent or not a number.</summary>
    public int Int(int i, int fallback) =>
        int.TryParse(Text(i), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>First character of the parameter in upper case, or <paramref name="fallback"/> when empty.</summary>
    public char Letter(int i, char fallback)
    {
        var t = Text(i);
        return t.Length > 0 ? char.ToUpperInvariant(t[0]) : fallback;
    }

    /// <summary>Y gives true, N gives false, anything else gives <paramref name="fallback"/>.</summary>
    public bool Flag(int i, bool fallback) => Letter(i, '\0') switch { 'Y' => true, 'N' => false, _ => fallback };

    /// <summary>Bar height from parameter <paramref name="i"/>; when absent or zero, the <c>^BY</c> height. Always within 1 to 32000.</summary>
    public int Height(int i)
    {
        var h = Int(i, 0);
        return Math.Clamp(h > 0 ? h : By.Height, 1, BarDefaults.MaxHeight);
    }
}
