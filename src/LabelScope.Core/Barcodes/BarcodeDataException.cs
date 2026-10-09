namespace LabelScope.Core.Barcodes;

/// <summary>
/// Thrown by an encoder when the field data cannot be written in that symbology. The message is plain language
/// ("Code 39 cannot hold the letter 'a'...") and goes to the user as a warning; the field is skipped.
/// </summary>
internal sealed class BarcodeDataException : Exception
{
    /// <summary>Creates the exception with a message that is safe to show to the user.</summary>
    public BarcodeDataException(string message) : base(message) { }
}
