using System.Collections.Frozen;
using System.Runtime.InteropServices;
using LabelScope.Core.Rendering;

namespace LabelScope.Core.Memory;

/// <summary>
/// Label setup a real printer keeps from one job to the next: print width (^PW), label length (^LL), label home (^LH),
/// print orientation (^PO), reverse-all (^LR), and the clock: the time set with ^ST, the ^SO2 / ^SO3 offsets and the
/// ^SL language, which the guide says a printer keeps until they are changed or it is switched off; ^MC, with the
/// label ^MCN keeps as the background of the next one; the ^CI character set with its remapping; and the command
/// prefixes and delimiter (^CC, ^CT, ^CD). Label programs often send these once, in a setup job of their own,
/// and the labels that follow rely on them; forgetting them would draw those labels on the wrong size.
/// Kept until LabelScope closes or the user clears printer memory, as a printer keeps them until it is switched off.
/// Safe to use from several threads.
/// </summary>
public sealed class PrinterSetup
{
    private readonly object _gate = new();
    private Values _values = Values.None;
    private LabelBackground? _background;

    /// <summary>
    /// Held while a job is drawn. A setup job (^PW, ^LL...) and the label after it may arrive on separate connections
    /// and be drawn at the same time; drawing jobs one after another means the label always sees the setup.
    /// </summary>
    internal object JobGate { get; } = new();

    /// <summary>The setup values; null means "never set" (that side of the label is then sized to what is drawn).</summary>
    /// <param name="WidthDots">Last ^PW, in dots.</param>
    /// <param name="HeightDots">Last ^LL, in dots.</param>
    /// <param name="HomeX">Last ^LH x, in dots.</param>
    /// <param name="HomeY">Last ^LH y, in dots.</param>
    /// <param name="Inverted">Last ^PO: true for ^POI (upside down).</param>
    /// <param name="ReverseAll">Last ^LR: true for ^LRY.</param>
    /// <param name="ClockOffset">
    /// What the last ^ST set, as the difference between the set time and the job's time in UTC (see
    /// <c>FieldClock.PrinterNow</c>): later jobs show the set time moved on by the time since, as a printer's battery
    /// clock does. Null when no ^ST was sent, so the clock is the job's own time.
    /// </param>
    /// <param name="ClockSecondary">Last ^SO2 offset; null when none was sent (the secondary clock is then unset).</param>
    /// <param name="ClockThird">Last ^SO3 offset; null when none was sent.</param>
    /// <param name="ClockLanguage">Last ^SL language (1-18); null when none was sent (English).</param>
    /// <param name="KeepImage">
    /// Last ^MC: true for ^MCN (each finished label is kept as the background of the next one), false for ^MCY; null when
    /// none was sent, which is ^MCY, the power-up value.
    /// </param>
    /// <param name="CharacterSet">
    /// Last ^CI, with its remapping pairs; null when none was sent, and the text is then drawn as received (the power-up
    /// set is a printer setting LabelScope cannot know). Set and pairs are one value, so a later ^CI without pairs
    /// replaces an earlier remapping instead of keeping it.
    /// </param>
    /// <param name="Prefixes">
    /// The format and control prefixes and the delimiter in force at the end of the last job (^CC, ^CT, ^CD and their ~
    /// forms); null when none was changed, which is ^, ~ and the comma. The next job is read with them.
    /// </param>
    public sealed record Values(int? WidthDots, int? HeightDots, int? HomeX, int? HomeY, bool? Inverted, bool? ReverseAll,
                                TimeSpan? ClockOffset = null, ClockShift? ClockSecondary = null, ClockShift? ClockThird = null,
                                int? ClockLanguage = null, bool? KeepImage = null, CharacterSetting? CharacterSet = null,
                                ZplPrefixes? Prefixes = null)
    {
        /// <summary>Nothing set yet.</summary>
        public static readonly Values None = new(null, null, null, null, null, null);
    }

    /// <summary>A consistent copy of the current setup.</summary>
    public Values Current
    {
        get { lock (_gate) return _values; }
    }

    /// <summary>Keeps every value <paramref name="changes"/> sets (non-null) and leaves the others as they were.</summary>
    internal void Apply(Values changes)
    {
        lock (_gate)
        {
            _values = new Values(
                changes.WidthDots ?? _values.WidthDots,
                changes.HeightDots ?? _values.HeightDots,
                changes.HomeX ?? _values.HomeX,
                changes.HomeY ?? _values.HomeY,
                changes.Inverted ?? _values.Inverted,
                changes.ReverseAll ?? _values.ReverseAll,
                changes.ClockOffset ?? _values.ClockOffset,
                changes.ClockSecondary ?? _values.ClockSecondary,
                changes.ClockThird ?? _values.ClockThird,
                changes.ClockLanguage ?? _values.ClockLanguage,
                changes.KeepImage ?? _values.KeepImage,
                changes.CharacterSet ?? _values.CharacterSet,
                changes.Prefixes ?? _values.Prefixes);
        }
    }

    /// <summary>
    /// The picture ^MCN kept: the last label printed while ^MCN was in effect, without its ^FV fields. It is drawn at
    /// 0,0 under every label that follows, until a label printed under ^MCY clears it. Null when nothing is kept.
    /// Kept apart from <see cref="Values"/>, whose null means "not changed" and so could never clear it.
    /// </summary>
    internal LabelBackground? KeptBackground
    {
        get { lock (_gate) return _background; }
        set { lock (_gate) _background = value; }
    }

    /// <summary>Forgets everything, the kept background included, as switching the printer off does.</summary>
    internal void Reset()
    {
        lock (_gate)
        {
            _values = Values.None;
            _background = null;
        }
    }
}

/// <summary>
/// A ^CI as a printer keeps it: the character set and its remapping pairs. Never changed once made.
/// </summary>
public sealed record CharacterSetting
{
    private static readonly IReadOnlyDictionary<char, char> NoRemap = FrozenDictionary<char, char>.Empty;

    /// <summary>Creates a setting; <paramref name="remap"/> is copied, so later changes to it do not leak in.</summary>
    /// <param name="set">The ^CI set number (0-36).</param>
    /// <param name="remap">
    /// Remapping pairs by byte value: when byte d (the key) arrives, the glyph at position s (the value) prints, as the
    /// guide's example ^CI0,21,36 prints € for $. Used with sets 0-13 only.
    /// </param>
    public CharacterSetting(int set, IReadOnlyDictionary<char, char> remap)
    {
        Set = set;
        Remap = remap.Count == 0 ? NoRemap : remap.ToFrozenDictionary();
    }

    /// <summary>The ^CI set number.</summary>
    public int Set { get; }

    /// <summary>Remapping pairs, destination byte to source position; empty when the ^CI had none.</summary>
    public IReadOnlyDictionary<char, char> Remap { get; }

    /// <summary>A set without remapping.</summary>
    internal static CharacterSetting Plain(int set) => new(set, NoRemap);
}

/// <summary>
/// A label picture kept by ^MCN, as one grey byte per dot (0 black, 255 white). A label is drawn in black and grey only,
/// so nothing is lost, and a 40-megapixel label (the largest LabelScope draws) costs 40 MB instead of the 160 MB of its
/// colour bitmap. Never changed once made, so the jobs and the serial images that draw it may share one instance; it
/// holds plain bytes rather than a Skia bitmap, which has a native lifetime and must not be shared between threads.
/// </summary>
internal sealed class LabelBackground
{
    private readonly byte[] _pixels;

    private LabelBackground(int width, int height, byte[] pixels)
    {
        Width = width;
        Height = height;
        _pixels = pixels;
    }

    /// <summary>Width in dots.</summary>
    public int Width { get; }

    /// <summary>Height in dots.</summary>
    public int Height { get; }

    /// <summary>The dots, row after row, <see cref="Width"/> bytes per row.</summary>
    public ReadOnlyMemory<byte> Pixels => _pixels;

    /// <summary>
    /// Copies a finished RGBA label bitmap (as the painters draw it) into a grey picture. Grey pixels keep their exact
    /// value, so the background draws back dot for dot as it was printed.
    /// </summary>
    /// <param name="rgba">The bitmap's pixel bytes, R, G, B, A per dot.</param>
    /// <param name="rowBytes">Bytes per bitmap row.</param>
    /// <param name="width">Width in dots.</param>
    /// <param name="height">Height in dots.</param>
    public static LabelBackground FromRgba(ReadOnlySpan<byte> rgba, int rowBytes, int width, int height)
    {
        var pixels = new byte[width * height];
        if (!LabelPng.TryToGray(rgba, rowBytes, width, height, pixels))
        {
            // Never the case for what the painters draw; should a pixel ever carry colour, its brightness is kept.
            for (var y = 0; y < height; y++)
            {
                var row = rgba.Slice(y * rowBytes, width * 4);
                for (var x = 0; x < width; x++)
                    pixels[y * width + x] = (byte)((row[x * 4] * 77 + row[x * 4 + 1] * 150 + row[x * 4 + 2] * 29) >> 8);
            }
        }
        return new LabelBackground(width, height, pixels);
    }

    /// <summary>
    /// Pins the pixels for a Skia call that reads them in place, so drawing a 40 MB background copies nothing. Free
    /// the handle once Skia is done with the address.
    /// </summary>
    internal GCHandle Pin() => GCHandle.Alloc(_pixels, GCHandleType.Pinned);
}
