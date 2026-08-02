using System.Globalization;
using System.Numerics;
using System.Text;

namespace EmbodySense.Core.Common.Capabilities;

internal static class CapabilityJsonNumberCanonicalizer
{
    internal static bool TryCanonicalize(string rawNumber, out string? canonicalNumber, out bool isNegativeZero)
    {
        canonicalNumber = null;
        isNegativeZero = false;
        if (string.IsNullOrEmpty(rawNumber))
        {
            return false;
        }

        var isNegative = rawNumber[0] == '-';
        var numberStart = isNegative ? 1 : 0;
        var exponentIndex = rawNumber.IndexOf('e');
        if (exponentIndex < 0)
        {
            exponentIndex = rawNumber.IndexOf('E');
        }

        if (exponentIndex < 0)
        {
            exponentIndex = rawNumber.Length;
        }

        var significand = rawNumber.AsSpan(numberStart, exponentIndex - numberStart);
        var decimalIndex = significand.IndexOf('.');
        var fractionalDigitCount = decimalIndex < 0 ? 0 : significand.Length - decimalIndex - 1;
        var digits = new StringBuilder(significand.Length);
        foreach (var character in significand)
        {
            if (character != '.')
            {
                digits.Append(character);
            }
        }

        var firstSignificantDigit = 0;
        while (firstSignificantDigit < digits.Length && digits[firstSignificantDigit] == '0')
        {
            firstSignificantDigit++;
        }

        if (firstSignificantDigit == digits.Length)
        {
            isNegativeZero = isNegative;
            canonicalNumber = "0";
            return true;
        }

        var lastSignificantDigit = digits.Length - 1;
        while (digits[lastSignificantDigit] == '0')
        {
            lastSignificantDigit--;
        }

        var exponent = BigInteger.Zero;
        if (exponentIndex < rawNumber.Length && !BigInteger.TryParse(rawNumber[(exponentIndex + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent))
        {
            return false;
        }

        var trailingZeroCount = digits.Length - lastSignificantDigit - 1;
        exponent += trailingZeroCount - fractionalDigitCount;
        var significantDigits = digits.ToString(firstSignificantDigit, lastSignificantDigit - firstSignificantDigit + 1);
        if (exponent.IsZero)
        {
            canonicalNumber = isNegative ? "-" + significantDigits : significantDigits;
            return true;
        }

        canonicalNumber = string.Concat(isNegative ? "-" : string.Empty, significantDigits, "e", exponent.ToString(CultureInfo.InvariantCulture));
        return true;
    }
}
