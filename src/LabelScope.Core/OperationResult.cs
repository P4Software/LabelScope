namespace LabelScope.Core;

/// <summary>Outcome of an action, with a message written for the end user.</summary>
/// <param name="Success">True when the action did what was asked.</param>
/// <param name="Message">Plain-language text: what happened and what to do next.</param>
public sealed record OperationResult(bool Success, string Message)
{
    /// <summary>Creates a successful result.</summary>
    public static OperationResult Ok(string message) => new(true, message);

    /// <summary>Creates a failed result.</summary>
    public static OperationResult Fail(string message) => new(false, message);
}
