using System.Globalization;

namespace OxQL.Model;

/// <summary>
/// The spellings of a decimal the engine reads and writes.
/// <para>
/// A decimal member may be stored as <c>Decimal128</c> or as a string, which the driver writes with
/// <c>decimal.ToString(InvariantInfo)</c>: the scale is preserved, so one thousand is
/// <c>"1000.00"</c> when the CLR value carried two decimal places and <c>"1000"</c> when it
/// carried none.
/// </para>
/// <para>
/// <see cref="Canonical"/> is the exact plain representation with no trailing fractional zeros,
/// which the wire carries. <see cref="ScalePattern"/> is the anchored regex that matches every
/// scale the driver could have written the same value with, which is how an equality reaches a
/// row that stores the value as a string.
/// </para>
/// </summary>
public static class DecimalText
{
    /// <summary>
    /// The canonical plain text of a decimal: fixed-point, exact, no exponent, no trailing
    /// fractional zeros, no trailing point. <c>125000.00m</c> and <c>125000m</c> both render
    /// <c>"125000"</c>; <c>0.0000001m</c> renders <c>"0.0000001"</c>.
    /// </summary>
    public static string Canonical(decimal value)
    {
        // F29 is fixed-point and never exponential, and 29 places is past decimal's 28-place
        // scale, so it is exact for every value; the padding zeros come off below.
        var text = value.ToString("F29", CultureInfo.InvariantCulture);
        var point = text.IndexOf('.', StringComparison.Ordinal);

        if (point < 0)
            return text;

        var end = text.Length;

        while (end > point + 1 && text[end - 1] == '0')
            end--;

        if (end == point + 1)
            end = point;

        return text[..end];
    }

    /// <summary>
    /// An anchored regex matching every text the driver could have written
    /// <paramref name="value"/> as: the canonical spelling with any number of trailing
    /// fractional zeros. <c>125000</c> matches <c>"125000"</c> and <c>"125000.00"</c>;
    /// <c>99999.99</c> matches <c>"99999.99"</c> and <c>"99999.9900"</c>.
    /// </summary>
    public static string ScalePattern(decimal value)
    {
        var canonical = Canonical(value);
        var escaped = canonical.Replace(".", "\\.", StringComparison.Ordinal);

        // A canonical spelling never carries a leading '+', a leading zero beyond "0." or an
        // exponent, so escaping the point is the whole of the escaping this needs.
        return canonical.Contains('.', StringComparison.Ordinal)
            ? "^" + escaped + "0*$"
            : "^" + escaped + "(?:\\.0+)?$";
    }
}
