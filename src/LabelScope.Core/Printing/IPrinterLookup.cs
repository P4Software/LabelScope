namespace LabelScope.Core.Printing;

/// <summary>A printer as the Windows print system reports it.</summary>
/// <param name="Name">Printer name.</param>
/// <param name="Comment">The printer's comment (LabelScope writes its owner marker here).</param>
/// <param name="PortName">Name of the port the printer prints to.</param>
public sealed record PrinterEntry(string Name, string Comment, string PortName);

/// <summary>Finds installed printers without starting any process; abstracted so printer logic can be tested.</summary>
public interface IPrinterLookup
{
    /// <summary>
    /// Returns the printer called <paramref name="printerName"/> (case does not matter), or null when there is none.
    /// </summary>
    /// <exception cref="InvalidOperationException">The print system could not be asked (spooler stopped, no Windows).</exception>
    PrinterEntry? Find(string printerName);
}
