using System.Globalization;
using System.Text.RegularExpressions;
using Tankstat.Domain.Recognition;
using Tankstat.Domain.Vehicles;

namespace Tankstat.Application.Recognition;

/// <summary>
/// Turns what a provider wrote into the values a reading keeps: invariant numbers rounded like the app stores them, ISO dates,
/// upper-case currency codes and trimmed titles. A value that does not fit (not a number, not positive, an impossible date) is
/// dropped rather than kept: a blank field is better than a wrong one.
/// </summary>
public static partial class ReadingNormaliser
{
    public static IReadOnlyList<ReadingValue> Normalise(IEnumerable<RecognizedValue> values) =>
        values.Select(v => Normalise(v.Name, v.Value) is { } text ? new ReadingValue(v.Name, text, v.Confidence, v.Source) : null)
            .OfType<ReadingValue>()
            .ToList();

    public static string? Normalise(ReadingFieldName name, string? raw)
    {
        var text = raw?.Trim() ?? "";
        if (text.Length == 0) return null;
        return name switch
        {
            ReadingFieldName.Odometer => long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var km)
                ? km.ToString(CultureInfo.InvariantCulture) : null,
            ReadingFieldName.Total => Amount(text, decimals: 2, max: 999_999_999_999.99m),
            ReadingFieldName.Volume => Amount(text, decimals: 3, max: 999_999.999m),
            ReadingFieldName.UnitPrice => Amount(text, decimals: 3, max: 999_999.999m),
            ReadingFieldName.Currency => CurrencyCode().IsMatch(text) ? text.ToUpperInvariant() : null,
            ReadingFieldName.Date => DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
            ReadingFieldName.Title => Title(text),
            ReadingFieldName.Discount => Amount(Unsigned(text), decimals: 2, max: 999_999_999_999.99m),
            _ => null,
        };
    }

    /// <summary>
    /// A discount read as nothing: what a model answers for "none" (0, -0.00, 0,00 with the receipt's comma, what rounds to 0 at the cent, or a
    /// word such as "none" or "N/A"), so it is left out without a reason to tell.
    /// </summary>
    public static bool IsNoDiscount(ReadingFieldName name, string? raw)
    {
        if (name != ReadingFieldName.Discount || raw is null) return false;
        if (NoneWords.Contains(raw.Trim())) return true;
        return decimal.TryParse(Unsigned(raw).Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            && Math.Round(value, 2, MidpointRounding.AwayFromZero) == 0;
    }

    private static readonly HashSet<string> NoneWords = new(StringComparer.OrdinalIgnoreCase) { "none", "n/a", "na", "null", "nil", "-", "–", "−", "—" };

    /// <summary>
    /// A discount as a positive amount: receipts print what was taken off with a minus sign before or after it ("-430", "4,30-"), a dash or
    /// in brackets, and models copy them; the amount is the same.
    /// </summary>
    private static string Unsigned(string text)
    {
        var value = text.Trim();
        if (value.Length > 1 && value[0] == '(' && value[^1] == ')') value = value[1..^1];
        return value.Trim().Trim('-', '–', '−').Trim();
    }

    private static string? Amount(string text, int decimals, decimal max)
    {
        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)) return null;
        value = Math.Round(value, decimals, MidpointRounding.AwayFromZero);
        return value > 0 && value <= max ? value.ToString("0." + new string('#', decimals), CultureInfo.InvariantCulture) : null;
    }

    private static string? Title(string text)
    {
        var single = Spaces().Replace(text, " ");
        return single.Length <= Expense.MaxTitleLength ? single : single[..Expense.MaxTitleLength].TrimEnd();
    }

    [GeneratedRegex("^[A-Za-z]{3}$")]
    private static partial Regex CurrencyCode();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
