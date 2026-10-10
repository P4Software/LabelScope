using LabelScope.Core.Graphics;
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
    // Created on first use, once: Lazy's default mode is thread-safe, and the factory never throws, so a bad font
    // cannot leave a cached exception behind that every later label would hit.
    private readonly Lazy<(SKTypeface? Face, SKData? Bytes)> _typeface;

    /// <summary>Keeps <paramref name="data"/>; the typeface is created when a label first uses the font.</summary>
    public StoredFont(byte[] data)
    {
        Data = data;
        _typeface = new Lazy<(SKTypeface? Face, SKData? Bytes)>(() => Load(data));
    }

    /// <summary>The font file as it arrived.</summary>
    public byte[] Data { get; }

    /// <summary>The font, or null when the data is not a font Skia can read.</summary>
    public SKTypeface? Typeface => _typeface.Value.Face;

    /// <inheritdoc />
    public override long SizeInBytes => Data.LongLength;

    /// <inheritdoc />
    public override string Kind => "font";

    /// <summary>
    /// Hands the downloaded bytes (hostile until proven otherwise: ~DY only checked their header) to Skia. The SKData
    /// is returned with the typeface so it lives as long as this object, because the typeface reads glyphs from it lazily.
    /// </summary>
    private static (SKTypeface? Face, SKData? Bytes) Load(byte[] bytes)
    {
        try
        {
            var data = SKData.CreateCopy(bytes);
            var face = SKTypeface.FromData(data);
            if (face is not null) return (face, data);
            data.Dispose();
            return (null, null);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            // A font parser failure must become the "could not be read" warning, never an error in the renderer.
            return (null, null);
        }
    }
}
