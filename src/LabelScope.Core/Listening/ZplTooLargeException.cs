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
        : base(Text.Get("Listener_TooLarge"))
    {
    }
}
