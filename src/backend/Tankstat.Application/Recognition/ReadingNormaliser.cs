using System.Globalization;
using System.Text.RegularExpressions;
using Tankstat.Domain.Recognition;
using Tankstat.Domain.Tyres;
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
            ReadingFieldName.TyreWork => TyreWork(text),
            _ => null,
        };
    }

    /// <summary>
    /// The tyre work a receipt can tell (#290, the owner's decision 66), as the model is asked to spell it: never <c>Other</c>, a note or a
    /// swap, which a receipt cannot tell apart. The kind's name, so the client takes it as the tyre log's kind.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, TyreEntryKind> TyreWorks = new Dictionary<string, TyreEntryKind>
    {
        ["puncture-repaired"] = TyreEntryKind.PunctureRepaired,
        ["valve-replaced"] = TyreEntryKind.ValveReplaced,
        ["wheel-balancing"] = TyreEntryKind.WheelBalancing,
        ["storage-fee"] = TyreEntryKind.StorageFee,
        ["bought"] = TyreEntryKind.Bought,
    };

    /// <summary>One of <see cref="TyreWorks"/>, whatever its case or separators (a model may write <c>Wheel balancing</c>); anything else is none.</summary>
    private static string? TyreWork(string text)
    {
        var key = string.Join('-', text.ToLowerInvariant().Split([' ', '_', '-'], StringSplitOptions.RemoveEmptyEntries));
        if (TyreWorks.TryGetValue(key, out var kind)) return kind.ToString();
        var bare = key.Replace("-", "", StringComparison.Ordinal); // the kind's own name, PunctureRepaired
        return TyreWorks.Values.Select(k => k.ToString()).FirstOrDefault(k => k.Equals(bare, StringComparison.OrdinalIgnoreCase));
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

    /// <summary>
    /// No tyre work (#290): what a model answers for a receipt that is not for any ("none", "other", "unknown", "no"), so it is left out
    /// without a reason to tell, as a discount of none is. Only a word of the five is tyre work; <c>other</c> never is (decision 66).
    /// </summary>
    public static bool IsNoTyreWork(ReadingFieldName name, string? raw) =>
        name == ReadingFieldName.TyreWork && raw is not null && (NoneWords.Contains(raw.Trim()) || NoTyreWords.Contains(raw.Trim()));

    private static readonly HashSet<string> NoTyreWords = new(StringComparer.OrdinalIgnoreCase) { "other", "unknown", "no", "false", "" };

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
