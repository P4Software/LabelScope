// src/LabelScope.Core/Listening/ZplTooLargeException.cs
namespace LabelScope.Core.Listening;

/// <summary>
/// Thrown by <see cref="ZplStreamSplitter.Feed"/> when a label grows beyond
/// <see cref="ZplStreamSplitter.MaxPendingChars"/> without an end marker. The message is
/// written for the operator, not the developer: it says what happened and what to check.
/// </summary>
public sealed class ZplTooLargeException : Exception
{
    /// <summary>Creates the exception with the plain-language message shown to the operator.</summary>
    public ZplTooLargeException()
        : base("A label larger than 16 MB was received without an end marker (^XZ) and was discarded. Check the program that sends the labels.")
    {
    }
}
