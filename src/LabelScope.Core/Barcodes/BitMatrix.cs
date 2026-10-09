namespace LabelScope.Core.Barcodes;

/// <summary>A grid of dark and light modules, used by the 2D symbologies. x runs left to right, y top to bottom.</summary>
internal sealed class BitMatrix
{
    private readonly bool[] _modules;

    /// <summary>Creates an all-light matrix.</summary>
    public BitMatrix(int width, int height)
    {
        Width = width;
        Height = height;
        _modules = new bool[width * height];
    }

    /// <summary>Number of columns.</summary>
    public int Width { get; }

    /// <summary>Number of rows.</summary>
    public int Height { get; }

    /// <summary>True for a dark module.</summary>
    public bool this[int x, int y]
    {
        get => _modules[Index(x, y)];
        set => _modules[Index(x, y)] = value;
    }

    // Checked per axis: a flat index alone would let x = Width silently wrap to the next row.
    private int Index(int x, int y)
    {
        if ((uint)x >= (uint)Width) throw new ArgumentOutOfRangeException(nameof(x));
        if ((uint)y >= (uint)Height) throw new ArgumentOutOfRangeException(nameof(y));
        return y * Width + x;
    }
}
