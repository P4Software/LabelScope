// src/LabelScope.Core/Barcodes/CheckDigits.cs
namespace LabelScope.Core.Barcodes;

/// <summary>The check digit formulas used by the supported symbologies.</summary>
internal static class CheckDigits
{
    /// <summary>The 43 Code 39 characters in value order (the value of a character is its index).</summary>
    public const string Code39Set = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ-. $/+%";

    /// <summary>Digit value of <paramref name="c"/>; anything else is a data error the user can fix.</summary>
    private static int Digit(char c) =>
        c is >= '0' and <= '9'
            ? c - '0'
            : throw new BarcodeDataException($"This barcode accepts digits only, but the data contains '{c}'.");

    /// <summary>
    /// Mod 10 as used by UPC, EAN, GS1 and Zebra: the rightmost data digit is weighted 3, the next 1, and so on.
    /// The check digit brings the weighted sum up to the next multiple of 10.
    /// </summary>
    public static int Mod10(string digits)
    {
        var sum = 0;
        var weight = 3;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            sum += Digit(digits[i]) * weight;
            weight = 4 - weight; // alternates 3, 1
        }
        return (10 - sum % 10) % 10;
    }

    /// <summary>
    /// MSI Mod 10 (IBM method): starting at the rightmost data digit take every second digit, double the number
    /// they form, add the digits of the result and the untouched digits; the check digit completes a multiple of 10.
    /// </summary>
    public static int Mod10Ibm(string digits)
    {
        var sum = 0;
        var doubleIt = true; // the rightmost data digit is doubled
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var d = Digit(digits[i]);
            if (doubleIt)
            {
                var doubled = d * 2;
                sum += doubled / 10 + doubled % 10; // sum of the digits of the doubled value
            }
            else sum += d;
            doubleIt = !doubleIt;
        }
        return (10 - sum % 10) % 10;
    }

    /// <summary>MSI Mod 11 (IBM weights 2 to 7, repeating from the right). The result 10 needs two printed digits.</summary>
    public static int Mod11Ibm(string digits)
    {
        var sum = 0;
        var weight = 2;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            sum += Digit(digits[i]) * weight;
            weight = weight == 7 ? 2 : weight + 1;
        }
        return (11 - sum % 11) % 11;
    }

    /// <summary>Code 39 Mod 43: the check character is the one whose index equals the sum of all values modulo 43.</summary>
    public static char Mod43(string data)
    {
        var sum = 0;
        foreach (var c in data)
        {
            var v = Code39Set.IndexOf(c);
            if (v < 0) throw new BarcodeDataException($"Code 39 cannot hold the character '{c}'.");
            sum += v;
        }
        return Code39Set[sum % 43];
    }

    /// <summary>Code 11 check values: C weights run 1 to 10 from the right, K weights 1 to 9 and include C.</summary>
    public static (int C, int K) Code11(string data)
    {
        var values = data.Select(c => c == '-' ? 10 : c is >= '0' and <= '9' ? c - '0'
            : throw new BarcodeDataException($"Code 11 holds digits and hyphens only, but the data contains '{c}'.")).ToList();
        var c11 = Weighted(values, 10, 11);
        values.Add(c11);
        return (c11, Weighted(values, 9, 11));
    }

    /// <summary>Code 93 check values C (weights 1 to 20) and K (weights 1 to 15, including C), both modulo 47.</summary>
    public static (int C, int K) Code93(int[] values)
    {
        var list = values.ToList();
        var c = Weighted(list, 20, 47);
        list.Add(c);
        return (c, Weighted(list, 15, 47));
    }

    /// <summary>Sum of value x weight with weights 1, 2, ... (wrapping after <paramref name="maxWeight"/>) from the right, modulo <paramref name="mod"/>.</summary>
    private static int Weighted(IReadOnlyList<int> values, int maxWeight, int mod)
    {
        var sum = 0;
        var weight = 1;
        for (var i = values.Count - 1; i >= 0; i--)
        {
            sum += values[i] * weight;
            weight = weight == maxWeight ? 1 : weight + 1;
        }
        return sum % mod;
    }
}
