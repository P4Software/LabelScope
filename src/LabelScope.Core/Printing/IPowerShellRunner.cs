namespace LabelScope.Core.Printing;

/// <summary>Runs PowerShell text; abstracted so printer logic can be tested without touching Windows.</summary>
public interface IPowerShellRunner
{
    /// <summary>
    /// Runs <paramref name="script"/> and returns what it wrote to its output.
    /// When <paramref name="elevated"/> is true Windows asks the user for permission first;
    /// declining throws <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<string> RunAsync(string script, bool elevated, CancellationToken ct = default);
}
