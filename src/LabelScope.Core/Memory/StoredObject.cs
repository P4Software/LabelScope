using LabelScope.Core.Graphics;
using LabelScope.Core.Rendering;
using SkiaSharp;

namespace LabelScope.Core.Memory;

/// <summary>Something kept in printer memory.</summary>
internal abstract class StoredObject
{
    /// <summary>Memory the object occupies, counted against <see cref="PrinterMemory.MaxBytes"/>.</summary>
    public abstract long SizeInBytes { get; }

    /// <summary>What the object is, in the words messages use ("graphic", "font").</summary>
    public abstract string Kind { get; }
}

/// <summary>A stored monochrome graphic (from ~DG, ~DY or ^IS).</summary>
internal sealed class StoredGraphic(MonoImage image) : StoredObject
{
    /// <summary>The graphic.</summary>
    public MonoImage Image { get; } = image;

    /// <inheritdoc />
    public override long SizeInBytes => Image.SizeInBytes;

    /// <inheritdoc />
    public override string Kind => "graphic";
}

/// <summary>A stored TrueType or OpenType font file (from ~DY).</summary>
internal sealed class StoredFont : StoredObject
{
    // The font file, copied once into Skia's memory when it is stored; the caller's byte[] is not kept, so the font
    // exists once in memory and SizeInBytes counts what is really held. The typeface reads its glyphs from this
    // block lazily, so it lives as long as this object.
    private readonly SKData _data;

    // Parsed on first use, once: Lazy's default mode is thread-safe, and the factory never throws, so a bad font
    // cannot leave a cached exception behind that every later label would hit.
    private readonly Lazy<SKTypeface?> _typeface;

    /// <summary>Copies <paramref name="data"/>; the typeface is created when a label first uses the font.</summary>
    public StoredFont(byte[] data)
    {
        SizeInBytes = data.LongLength;
        _data = SKData.CreateCopy(data);
        _typeface = new Lazy<SKTypeface?>(() => Load(_data));
    }

    /// <summary>The font, or null when the data is not a font Skia can read.</summary>
    public SKTypeface? Typeface => _typeface.Value;

    /// <inheritdoc />
    public override long SizeInBytes { get; }

    /// <inheritdoc />
    public override string Kind => "font";

    /// <summary>Hands the downloaded bytes (hostile until proven otherwise: ~DY only checked their header) to Skia.</summary>
    private static SKTypeface? Load(SKData data)
    {
        try
        {
            return SKTypeface.FromData(data);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            // A font parser failure must become the "could not be read" warning, never an error in the renderer.
            return null;
        }
    }
}

/// <summary>
/// A label format stored with ^DF: the commands that followed the ^DF, kept as parsed commands so ^XF can insert them
/// into a later label without reading the text again. A printer keeps the format as text and reads it at print time;
/// the parsed commands give the same picture.
/// </summary>
internal sealed class StoredFormat(IReadOnlyList<ZplCommand> commands) : StoredObject
{
    /// <summary>The stored commands, in order, with the lines they had in the job that stored them.</summary>
    public IReadOnlyList<ZplCommand> Commands { get; } = commands;

    /// <summary>
    /// Roughly the text the format occupies: every name and argument, plus 16 bytes per command for its bookkeeping,
    /// so a format of a million empty commands still counts against the memory limit.
    /// </summary>
    public override long SizeInBytes { get; } = commands.Sum(c => (long)c.Name.Length + c.Args.Length + 16);

    /// <inheritdoc />
    public override string Kind => "format";
}
