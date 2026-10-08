namespace LabelScope.Core.Rendering;

/// <summary>One ZPL command such as <c>^FO10,20</c>.</summary>
/// <param name="Name">Prefix plus command letters in upper case, e.g. "^FO" or "~DG".</param>
/// <param name="Args">Everything after the name up to the next command.</param>
/// <param name="Line">1-based line of the ZPL text where the command starts.</param>
public sealed record ZplCommand(string Name, string Args, int Line);
