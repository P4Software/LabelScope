using LabelScope.Core.Graphics;

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
internal sealed class StoredFont(byte[] data) : StoredObject
{
    /// <summary>The font file as it arrived.</summary>
    public byte[] Data { get; } = data;

    /// <inheritdoc />
    public override long SizeInBytes => Data.LongLength;

    /// <inheritdoc />
    public override string Kind => "font";
}
