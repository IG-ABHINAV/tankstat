using System.Globalization;
using Tankstat.Domain.Recognition;

namespace Tankstat.Application.Recognition;

/// <summary>
/// What makes a value a model read believable. A model rates its own values, but those ratings are not calibrated: what the app can check
/// decides instead (an odometer against the vehicle's latest reading, litres × price against the total, a date against today, plain
/// ranges), and the model's rating only ever lowers the result. A value that fails a check is dropped (a blank beats a wrong value); one
/// that is possible but unlikely is capped at <see cref="Doubtful"/>, below what the app fills in by default. Every drop and every doubt
/// comes with its reason (<see cref="ReadingIssue"/>), so that "nothing was filled in" can be explained.
/// </summary>
public static class ReadingChecks
{
    /// <summary>The confidence of a value that is possible but not to be believed on its own: below the default <see cref="RecognitionOptions.MinConfidence"/>.</summary>
    public const double Doubtful = 0.5;

    /// <summary>Up to this far above the latest reading an odometer is simply the next one; beyond it, it is doubted.</summary>
    private const long OdometerNear = 50_000;

    /// <summary>More than this above the latest reading is not the same car between two logs: a misread.</summary>
    private const long OdometerRange = 100_000;

    /// <summary>Receipts are dated between this many years back and tomorrow (tomorrow covers every time zone).</summary>
    private const int DateYearsBack = 3;

    /// <summary>The values a photo of that kind can provide (none for <see cref="DocumentKind.Unknown"/>).</summary>
    public static IReadOnlyList<ReadingFieldName> FieldsOf(DocumentKind kind) => kind switch
    {
        DocumentKind.Odometer => [ReadingFieldName.Odometer],
        DocumentKind.FuelReceipt => [ReadingFieldName.Total, ReadingFieldName.Volume, ReadingFieldName.UnitPrice, ReadingFieldName.Discount, ReadingFieldName.Currency, ReadingFieldName.Date],
        DocumentKind.ExpenseReceipt => [ReadingFieldName.Total, ReadingFieldName.Currency, ReadingFieldName.Date, ReadingFieldName.Title],
        _ => [],
    };

    /// <summary>
    /// The values worth keeping of what a model read: those the kind of photo can show, normalised, with the confidence the checks allow
    /// (never more than the model's own). A fuel receipt's litres and price are checked against its total too. What was dropped or doubted
    /// comes back with its reason; a value the kind of photo cannot show (the model listed more than it was asked for) is only left out.
    /// </summary>
    public static CheckedValues Check(DocumentKind kind, IEnumerable<RecognizedValue> values, RecognitionRequest hints)
    {
        var allowed = FieldsOf(kind);
        var kept = new List<RecognizedValue>();
        var issues = new List<ReadingIssue>();
        foreach (var value in values)
        {
            if (!allowed.Contains(value.Name)) continue;
            if (ReadingNormaliser.IsNoDiscount(value.Name, value.Value)) continue; // "none", said as 0: nothing to keep, nothing to explain
            if (ReadingNormaliser.Normalise(value.Name, value.Value) is not { } text)
            {
                issues.Add(new ReadingIssue(value.Name, ReadingIssueCode.NotUnderstood));
                continue;
            }
            var verdict = Believable(value.Name, text, hints);
            if (verdict.Issue is { } code) issues.Add(new ReadingIssue(value.Name, code));
            if (verdict.Cap is not { } cap) continue;
            var rated = Rated(value.Confidence);
            if (rated is null) issues.Add(new ReadingIssue(value.Name, ReadingIssueCode.NoConfidence));
            kept.Add(value with { Value = text, Confidence = Math.Min(rated ?? Doubtful, cap) });
        }
        var withDiscount = DiscountChecked(kept, issues);
        return new CheckedValues(kind == DocumentKind.FuelReceipt ? CrossChecked(withDiscount, issues, hints) : withDiscount, issues);
    }

    /// <summary>The model's own rating on the scale of 0 to 1: one given in per cent is scaled down; null when it is no rating at all (none given, not a number, on no scale), which counts as doubtful.</summary>
    private static double? Rated(double confidence)
    {
        if (double.IsNaN(confidence)) return null;
        return confidence switch
        {
            > 1 and <= 100 => confidence / 100,
            > 100 or < 0 => null,
            _ => confidence,
        };
    }

    /// <summary>What a check says of one value: how far it can be believed on its own (null: not at all, it is dropped) and, when that is not simply "fully", why.</summary>
    private readonly record struct Verdict(double? Cap, ReadingIssueCode? Issue = null)
    {
        public static readonly Verdict Fine = new(1);

        public static Verdict Drop(ReadingIssueCode issue) => new(null, issue);

        public static Verdict Doubt(ReadingIssueCode issue) => new(Doubtful, issue);
    }

    private static Verdict Believable(ReadingFieldName name, string text, RecognitionRequest hints) => name switch
    {
        ReadingFieldName.Odometer => Odometer(long.Parse(text, CultureInfo.InvariantCulture), hints.LastOdometer),
        ReadingFieldName.Date => Date(DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture), hints.Today),
        ReadingFieldName.Volume => Amount(text) is >= 0.3m and <= 400m ? Verdict.Fine : Verdict.Drop(ReadingIssueCode.OutOfRange), // a moped's top-up to a lorry's tank
        ReadingFieldName.Total or ReadingFieldName.Discount => Amount(text) < 10_000_000m ? Verdict.Fine : Verdict.Drop(ReadingIssueCode.OutOfRange),
        ReadingFieldName.UnitPrice => Amount(text) < 100_000m ? Verdict.Fine : Verdict.Drop(ReadingIssueCode.OutOfRange),
        _ => Verdict.Fine,
    };

    /// <summary>An odometer never goes back, and between two logs it does not advance a hundred thousand; without a latest reading there is nothing to check against.</summary>
    private static Verdict Odometer(long value, long? last)
    {
        if (last is not { } latest) return Verdict.Fine;
        if (value < latest) return Verdict.Drop(ReadingIssueCode.OdometerBelowLatest);
        if (value - latest > OdometerRange) return Verdict.Drop(ReadingIssueCode.OdometerTooFarAbove);
        return value - latest > OdometerNear ? Verdict.Doubt(ReadingIssueCode.OdometerFarAbove) : Verdict.Fine;
    }

    private static Verdict Date(DateOnly date, DateOnly today) =>
        date > today.AddDays(1) ? Verdict.Drop(ReadingIssueCode.DateInFuture)
        : date < today.AddYears(-DateYearsBack) ? Verdict.Drop(ReadingIssueCode.DateTooOld)
        : Verdict.Fine;

    /// <summary>
    /// Several discounts printed for one fuel (a card's and a coupon's) are one discount, their sum, as sure as the least sure of them; and a
    /// discount is not believed above half the total read with it (a model that copied the total, or read a points line): it is dropped.
    /// </summary>
    private static List<RecognizedValue> DiscountChecked(List<RecognizedValue> kept, List<ReadingIssue> issues)
    {
        var discounts = kept.Where(v => v.Name == ReadingFieldName.Discount).ToList();
        if (discounts.Count == 0) return kept;
        var sum = discounts.Sum(d => Amount(d.Value));
        var discount = discounts[0] with { Value = sum.ToString("0.##", CultureInfo.InvariantCulture), Confidence = discounts.Min(d => d.Confidence) };
        var rest = kept.Where(v => v.Name != ReadingFieldName.Discount).ToList();
        if (rest.FirstOrDefault(v => v.Name == ReadingFieldName.Total) is { } total && sum > Amount(total.Value) / 2)
        {
            issues.Add(new ReadingIssue(ReadingFieldName.Discount, ReadingIssueCode.OutOfRange));
            return rest;
        }
        return [.. rest, discount];
    }

    /// <summary>
    /// The litres, the price per litre and the total read, checked against each other: litres × price − the discount must give the total. A
    /// discount counts only when it is rated above <see cref="Doubtful"/> (an unsure one is not filled in, so it must not blank what is sure).
    /// Without one, within <see cref="Loose"/> (a small discount nobody read still fits); with one, to the receipt's rounding
    /// (<see cref="Tight"/>), or a discount the total already lacks would fit as well. When litres × price fits the total without the discount
    /// (<see cref="Loose"/>), the total is before the discount or the discount was misread, which the numbers cannot tell apart: the total and
    /// the discount are doubted (a blank beats a wrong value), the litres and the price stay. When nothing fits, one of the amounts is misread,
    /// most likely the litres or the price, so those are doubted, the discount with them (this sum is the one check it has), and the total stays.
    /// </summary>
    private static List<RecognizedValue> CrossChecked(List<RecognizedValue> kept, List<ReadingIssue> issues, RecognitionRequest hints)
    {
        RecognizedValue? Of(ReadingFieldName name) => kept.FirstOrDefault(v => v.Name == name);
        if (Of(ReadingFieldName.Total) is not { } total || Of(ReadingFieldName.Volume) is not { } volume || Of(ReadingFieldName.UnitPrice) is not { } price) return kept;
        var currency = Of(ReadingFieldName.Currency)?.Value ?? hints.Currency;
        var discount = Of(ReadingFieldName.Discount) is { Confidence: > Doubtful } sure ? sure : null;
        var product = Amount(volume.Value) * Amount(price.Value);
        var paid = Amount(total.Value);
        if (Fits(paid, product - (discount is null ? 0 : Amount(discount.Value)), currency, discount is null ? Loose : Tight)) return kept;
        var (doubted, code) = discount is not null && Fits(paid, product, currency, Loose)
            ? (new[] { ReadingFieldName.Total, ReadingFieldName.Discount }, ReadingIssueCode.DiscountNotTakenOff)
            : (discount is null ? [ReadingFieldName.Volume, ReadingFieldName.UnitPrice] : new[] { ReadingFieldName.Volume, ReadingFieldName.UnitPrice, ReadingFieldName.Discount },
                ReadingIssueCode.AmountsDoNotAdd);
        issues.AddRange(doubted.Select(name => new ReadingIssue(name, code)));
        return kept.Select(v => doubted.Contains(v.Name) ? v with { Confidence = Math.Min(v.Confidence, Doubtful) } : v).ToList();
    }

    /// <summary>How far a total may be from litres × price when no discount was read: 1.5 % (a small discount nobody read).</summary>
    private const decimal Loose = 0.015m;

    /// <summary>How far it may be once a discount was read: the receipt's rounding (a price per litre printed to a tenth, a total to the unit).</summary>
    private const decimal Tight = 0.002m;

    /// <summary>Whether a total and litres × price per litre (less a discount) agree: within the share of the total, or one and a half of the currency's smallest unit.</summary>
    private static bool Fits(decimal total, decimal product, string? currency, decimal share)
    {
        var unit = currency?.ToUpperInvariant() is "HUF" or "JPY" or "KRW" or "ISK" ? 1m : 0.01m; // whole units, or cents
        return Math.Abs(total - product) <= Math.Max(total * share, unit * 1.5m);
    }

    private static decimal Amount(string normalised) => decimal.Parse(normalised, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
}

/// <summary>What the checks made of a model's values: those worth keeping (normalised, with the confidence allowed) and why the others were dropped or doubted.</summary>
public sealed record CheckedValues(IReadOnlyList<RecognizedValue> Kept, IReadOnlyList<ReadingIssue> Issues);
