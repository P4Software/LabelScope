using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LabelScope.Core.Printing;

/// <summary>Looks printers up with the Windows <c>EnumPrinters</c> call. Needs no administrator rights.</summary>
public sealed class WinspoolPrinterLookup : IPrinterLookup
{
    private const uint PrinterEnumLocal = 0x00000002;        // printers installed on this computer
    private const uint PrinterEnumConnections = 0x00000004;  // printers connected from a print server
    private const int ErrorInsufficientBuffer = 122;

    // Layout of PRINTER_INFO_2 (winspool.h). Only the first strings are read; the rest keeps the size right.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PrinterInfo2
    {
        public string? ServerName;
        public string? PrinterName;
        public string? ShareName;
        public string? PortName;
        public string? DriverName;
        public string? Comment;
        public string? Location;
        public IntPtr DevMode;
        public string? SepFile;
        public string? PrintProcessor;
        public string? Datatype;
        public string? Parameters;
        public IntPtr SecurityDescriptor;
        public uint Attributes;
        public uint Priority;
        public uint DefaultPriority;
        public uint StartTime;
        public uint UntilTime;
        public uint Status;
        public uint Jobs;
        public uint AveragePpm;
    }

    [DllImport("winspool.drv", EntryPoint = "EnumPrintersW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool EnumPrinters(uint flags, string? name, uint level, IntPtr printerEnum, uint bufferSize,
        out uint needed, out uint returned);

    /// <inheritdoc />
    public PrinterEntry? Find(string printerName) =>
        Enumerate().FirstOrDefault(p => string.Equals(p.Name, printerName, StringComparison.OrdinalIgnoreCase));

    /// <summary>The names of all printers, for diagnostics and tests.</summary>
    /// <exception cref="InvalidOperationException">The print system could not be asked.</exception>
    internal IReadOnlyList<string> ListNames() => Enumerate().Select(p => p.Name).ToList();

    private static List<PrinterEntry> Enumerate()
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("The Windows print system is not available on this computer.");

        const uint flags = PrinterEnumLocal | PrinterEnumConnections;

        // The call is made twice: first to learn the buffer size, then to fill it. A printer added in between
        // makes the second call ask for more room, hence the small retry loop.
        uint size = 0;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = size == 0 ? IntPtr.Zero : Marshal.AllocHGlobal((int)size);
            try
            {
                if (EnumPrinters(flags, null, 2, buffer, size, out var needed, out var returned))
                {
                    var result = new List<PrinterEntry>();
                    var itemSize = Marshal.SizeOf<PrinterInfo2>();
                    for (var i = 0; i < returned; i++)
                    {
                        var info = Marshal.PtrToStructure<PrinterInfo2>(buffer + i * itemSize);
                        if (!string.IsNullOrEmpty(info.PrinterName))
                            result.Add(new PrinterEntry(info.PrinterName!, info.Comment ?? "", info.PortName ?? ""));
                    }
                    return result;
                }

                var error = Marshal.GetLastWin32Error();
                if (error != ErrorInsufficientBuffer)
                    throw new InvalidOperationException("Windows could not list the printers: " + new Win32Exception(error).Message);
                size = needed;
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            }
        }
        throw new InvalidOperationException("Windows could not list the printers because the list kept changing. Try again.");
    }
}
