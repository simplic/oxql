using System.Globalization;

namespace OxQL.Model;

/// <summary>
/// The one spelling of a decimal the engine uses, on the way in and on the way out.
/// <para>
/// A decimal member may be stored as <c>Decimal128</c> or — on rows written before the
/// migration — as a string, which the driver writes with
/// <c>decimal.ToString(InvariantInfo)</c>: the scale is preserved, so one thousand is
/// <c>"1000.00"</c> when the CLR value carried two decimal places and <c>"1000"</c> when it
/// carried none. <c>ToString("G29")</c> is not that spelling: it drops trailing zeros
/// (<c>125000.00m</c> becomes <c>"125000"</c>) and switches to exponential notation below
/// 1e-5 (<c>0.0000001m</c> becomes <c>"1E-07"</c>), neither of which the driver ever writes.
/// An operand built with <c>G29</c> therefore cannot match a string-stored row whose text
/// carries a trailing zero, and a value read out of a row does not find the row it came from.
/// </para>
/// <para>
/// <see cref="Canonical"/> is the exact plain representation with no trailing fractional
/// zeros, and it is what both <c>OperandCoercer</c> and <c>WireEncoder</c> use, so a value
/// the engine hands a caller is a value the engine accepts back.
/// <see cref="ScalePattern"/> is the anchored regex that matches every scale the driver
/// could have written the same value with, which is how equality reaches the string bracket
/// without enumerating spellings.
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
