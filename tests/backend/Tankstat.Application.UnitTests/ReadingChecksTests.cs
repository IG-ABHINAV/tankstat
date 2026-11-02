using Tankstat.Application.Recognition;
using Tankstat.Domain.Recognition;

namespace Tankstat.Application.UnitTests;

/// <summary>What the app believes of a model's reading: the checks it can make decide, the model's own confidence only lowers the result.</summary>
public class ReadingChecksTests
{
    private static readonly DateOnly Today = new(2026, 10, 1);

    private static RecognitionRequest Hints(long? lastOdometer = 200_000, string? currency = "HUF") =>
        new(new byte[] { 1 }, "image/jpeg", new HashSet<DocumentKind> { DocumentKind.Odometer, DocumentKind.FuelReceipt }, "hu", lastOdometer, currency, Today);

    private static RecognizedValue Read(ReadingFieldName name, string value, double confidence = 0.9) => new(name, value, confidence, ValueSource.Read);

    private static string Shown(IEnumerable<RecognizedValue> values) => string.Join(" ", values.Select(v => $"{v.Name}={v.Value}@{v.Confidence}"));

    private static IReadOnlyList<RecognizedValue> Apply(DocumentKind kind, IEnumerable<RecognizedValue> values, RecognitionRequest hints) => ReadingChecks.Check(kind, values, hints).Kept;

    private static string Why(DocumentKind kind, IEnumerable<RecognizedValue> values, RecognitionRequest hints) =>
        string.Join(" ", ReadingChecks.Check(kind, values, hints).Issues.Select(i => $"{i.Field?.ToString() ?? "-"}:{i.Code}"));

    [Fact]
    public void OnlyTheValuesThatKindOfPhotoShows_AreKept_Normalised()
    {
        var kept = Apply(DocumentKind.Odometer, [Read(ReadingFieldName.Odometer, "212345"), Read(ReadingFieldName.Total, "24687")], Hints());

        Assert.Equal("Odometer=212345@0.9", Shown(kept));
        Assert.Empty(Apply(DocumentKind.Unknown, [Read(ReadingFieldName.Odometer, "212345")], Hints()));
        Assert.Empty(Apply(DocumentKind.FuelReceipt, [Read(ReadingFieldName.Volume, "38,52")], Hints())); // not a number the app keeps
        Assert.Equal("Total=24687", Shown(Apply(DocumentKind.ExpenseReceipt, [Read(ReadingFieldName.Total, "24687.00", 1)], Hints())).Split('@')[0]);
    }

    [Theory]
    [InlineData(199_999, null)] // below the latest reading: an odometer never goes back
    [InlineData(200_000, 0.9)]
    [InlineData(250_000, 0.9)]
    [InlineData(250_001, 0.5)] // possible between two logs, but not to be believed on its own
    [InlineData(300_000, 0.5)]
    [InlineData(300_001, null)] // more than a hundred thousand since the last log: misread
    public void AnOdometer_IsBelievedAboveTheLatestReading_AndDoubtedFarAboveIt(long value, double? confidence)
    {
        var kept = Apply(DocumentKind.Odometer, [Read(ReadingFieldName.Odometer, value.ToString())], Hints());

        Assert.Equal(confidence, kept.SingleOrDefault()?.Confidence);
    }

    [Theory]
    [InlineData(199_999, "Odometer:OdometerBelowLatest")]
    [InlineData(200_000, "")]
    [InlineData(250_000, "")]
    [InlineData(250_001, "Odometer:OdometerFarAbove")]
    [InlineData(300_000, "Odometer:OdometerFarAbove")]
    [InlineData(300_001, "Odometer:OdometerTooFarAbove")]
    public void AnOdometerThatIsDroppedOrDoubted_SaysWhy(long value, string issues) =>
        Assert.Equal(issues, Why(DocumentKind.Odometer, [Read(ReadingFieldName.Odometer, value.ToString())], Hints()));

    [Theory]
    [InlineData("123 456 km")]
    [InlineData("123.456")]
    [InlineData("123456.7")]
    [InlineData("-5")]
    public void AValueNotWrittenTheWayTheAppTakesIt_IsDroppedAsNotUnderstood(string text)
    {
        var checks = ReadingChecks.Check(DocumentKind.Odometer, [Read(ReadingFieldName.Odometer, text)], Hints());

        Assert.Empty(checks.Kept);
        Assert.Equal([new ReadingIssue(ReadingFieldName.Odometer, ReadingIssueCode.NotUnderstood)], checks.Issues);
    }

    [Fact]
    public void AVehiclesFirstOdometer_HasNothingToBeCheckedAgainst_AndIsTakenAsRead()
    {
        var kept = Apply(DocumentKind.Odometer, [Read(ReadingFieldName.Odometer, "999999", 0.8)], Hints(lastOdometer: null));

        Assert.Equal("Odometer=999999@0.8", Shown(kept));
    }

    [Theory]
    [InlineData("2026-10-02", true)] // tomorrow, in some time zone
    [InlineData("2026-10-03", false)]
    [InlineData("2023-10-01", true)]
    [InlineData("2023-09-30", false)]
    public void AReceiptsDate_MustBeRecent_AndNeverInTheFuture(string date, bool kept) =>
        Assert.Equal(kept, Apply(DocumentKind.FuelReceipt, [Read(ReadingFieldName.Date, date)], Hints()).Count == 1);

    [Theory]
    [InlineData("2026-10-03", "Date:DateInFuture")]
    [InlineData("2023-09-30", "Date:DateTooOld")]
    [InlineData("2026-09-30", "")]
    public void ADateThatIsDropped_SaysWhy(string date, string issues) =>
        Assert.Equal(issues, Why(DocumentKind.FuelReceipt, [Read(ReadingFieldName.Date, date)], Hints()));

    [Theory]
    [InlineData(ReadingFieldName.Volume, "0.2")]
    [InlineData(ReadingFieldName.Volume, "401")]
    [InlineData(ReadingFieldName.Total, "10000000")]
    [InlineData(ReadingFieldName.UnitPrice, "100000")]
    public void AnAmountNoFillUpHas_IsDropped_AsOutOfRange(ReadingFieldName name, string value)
    {
        Assert.Empty(Apply(DocumentKind.FuelReceipt, [Read(name, value)], Hints()));
        Assert.Equal($"{name}:OutOfRange", Why(DocumentKind.FuelReceipt, [Read(name, value)], Hints()));
    }

    [Fact]
    public void AValueTheKindOfPhotoCannotShow_IsLeftOutWithoutAReason_ButWhatIsKeptIsNot()
    {
        var checks = ReadingChecks.Check(DocumentKind.Odometer, [Read(ReadingFieldName.Total, "24687"), Read(ReadingFieldName.Odometer, "212345")], Hints());

        Assert.Equal("Odometer=212345@0.9", Shown(checks.Kept));
        Assert.Empty(checks.Issues); // nobody is waiting for a total from a dashboard
    }

    [Fact]
    public void LitresTimesPrice_MustFitTheTotal_OrBothAreDoubted_WhileTheTotalIsKept()
    {
        RecognizedValue[] fitting = [Read(ReadingFieldName.Total, "24687"), Read(ReadingFieldName.Volume, "38.52"), Read(ReadingFieldName.UnitPrice, "640.9"), Read(ReadingFieldName.Currency, "HUF")];
        RecognizedValue[] misread = [Read(ReadingFieldName.Total, "30000"), Read(ReadingFieldName.Volume, "38.52"), Read(ReadingFieldName.UnitPrice, "640.9"), Read(ReadingFieldName.Currency, "HUF")];

        Assert.Equal("Total=24687@0.9 Volume=38.52@0.9 UnitPrice=640.9@0.9 Currency=HUF@0.9", Shown(Apply(DocumentKind.FuelReceipt, fitting, Hints())));
        Assert.Equal("Total=30000@0.9 Volume=38.52@0.5 UnitPrice=640.9@0.5 Currency=HUF@0.9", Shown(Apply(DocumentKind.FuelReceipt, misread, Hints())));
        Assert.Equal("", Why(DocumentKind.FuelReceipt, fitting, Hints()));
        Assert.Equal("Volume:AmountsDoNotAdd UnitPrice:AmountsDoNotAdd", Why(DocumentKind.FuelReceipt, misread, Hints()));
    }

    [Theory]
    [InlineData("34744", "430", "", null)] // the receipt of #204: 43 L × 818 − 430 is what was paid
    [InlineData("34744", null, "", null)] // no discount read: 1.2 % off still fits the loose bar, as before
    // Litres × price alone fits: the total is before the discount, or the discount is misread. Neither can be told, so both are doubted.
    [InlineData("35174", "430", "Total Discount", ReadingIssueCode.DiscountNotTakenOff)]
    [InlineData("34744", "1000", "Total Discount", ReadingIssueCode.DiscountNotTakenOff)]
    [InlineData("39612", null, "Volume UnitPrice", ReadingIssueCode.AmountsDoNotAdd)] // the basket's grand total: still doubted, the total kept
    [InlineData("39612", "430", "Volume UnitPrice Discount", ReadingIssueCode.AmountsDoNotAdd)]
    public void ADiscountRead_IsTakenOffLitresTimesPrice_AndTheReceiptMustThenAddUpToItsRounding(string total, string? discount, string doubted, ReadingIssueCode? code)
    {
        List<RecognizedValue> values = [Read(ReadingFieldName.Total, total), Read(ReadingFieldName.Volume, "43"), Read(ReadingFieldName.UnitPrice, "818"), Read(ReadingFieldName.Currency, "HUF")];
        if (discount is not null) values.Add(Read(ReadingFieldName.Discount, discount));

        var checks = ReadingChecks.Check(DocumentKind.FuelReceipt, values, Hints());

        Assert.Equal(doubted, string.Join(" ", checks.Kept.Where(v => v.Confidence < 0.9).Select(v => v.Name)));
        Assert.Equal(doubted, string.Join(" ", checks.Issues.Select(i => i.Field)));
        Assert.All(checks.Issues, i => Assert.Equal(code, i.Code));
    }

    [Fact]
    public void AnUnsureDiscount_IsLeftOutOfTheSum_SoItNeverBlanksLitresAndAPriceThatFit()
    {
        RecognizedValue[] values = [Read(ReadingFieldName.Total, "34744"), Read(ReadingFieldName.Volume, "43"), Read(ReadingFieldName.UnitPrice, "818"), Read(ReadingFieldName.Discount, "10", 0.3)];

        var checks = ReadingChecks.Check(DocumentKind.FuelReceipt, values, Hints());

        Assert.Equal("Total=34744@0.9 Volume=43@0.9 UnitPrice=818@0.9 Discount=10@0.3", Shown(checks.Kept)); // the loose bar, as without one
        Assert.Empty(checks.Issues);
    }

    [Fact]
    public void SeveralDiscountsForTheFuel_AreOne_TheirSum_AsSureAsTheLeastSure()
    {
        RecognizedValue[] values = [Read(ReadingFieldName.Total, "34744"), Read(ReadingFieldName.Volume, "43"), Read(ReadingFieldName.UnitPrice, "818"),
            Read(ReadingFieldName.Discount, "-200"), Read(ReadingFieldName.Discount, "230", 0.8)]; // a card's and a coupon's

        var checks = ReadingChecks.Check(DocumentKind.FuelReceipt, values, Hints());

        Assert.Equal("Total=34744@0.9 Volume=43@0.9 UnitPrice=818@0.9 Discount=430@0.8", Shown(checks.Kept));
        Assert.Empty(checks.Issues);
    }

    [Theory]
    [InlineData(DocumentKind.FuelReceipt)]
    public void ADiscountAboveHalfTheTotal_IsNotBelieved(DocumentKind kind)
    {
        var checks = ReadingChecks.Check(kind, [Read(ReadingFieldName.Total, "34744"), Read(ReadingFieldName.Discount, "34744")], Hints()); // the total copied

        Assert.Equal("Total=34744@0.9", Shown(checks.Kept));
        Assert.Equal("Discount:OutOfRange", string.Join(" ", checks.Issues.Select(i => $"{i.Field}:{i.Code}")));
    }

    [Theory]
    [InlineData("-430", "430")] // printed with its minus sign, and copied
    [InlineData("430-", "430")] // after it, as German and Austrian receipts do
    [InlineData("–430", "430")] // with a dash
    [InlineData("(430)", "430")] // in brackets
    [InlineData("− 430,00", null)] // not written the way the app takes it: dropped as not understood
    [InlineData("430.004", "430")]
    public void ADiscount_IsKeptAsAPositiveAmount(string read, string? kept)
    {
        var checks = ReadingChecks.Check(DocumentKind.FuelReceipt, [Read(ReadingFieldName.Discount, read)], Hints());

        Assert.Equal(kept, checks.Kept.SingleOrDefault()?.Value);
        Assert.Equal(kept is null ? "Discount:NotUnderstood" : "", string.Join(" ", checks.Issues.Select(i => $"{i.Field}:{i.Code}")));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-0.00")]
    [InlineData("0,00")] // with the receipt's comma
    [InlineData("0.004")] // rounds to nothing at the cent
    [InlineData("none")]
    [InlineData("N/A")]
    [InlineData("-")]
    public void ADiscountOfNothing_IsLeftOut_WithoutAReason(string read)
    {
        var checks = ReadingChecks.Check(DocumentKind.FuelReceipt, [Read(ReadingFieldName.Discount, read), Read(ReadingFieldName.Total, "34744")], Hints());

        Assert.Equal("Total=34744@0.9", Shown(checks.Kept));
        Assert.Empty(checks.Issues); // a model's "none" is no reading problem to tell the person
    }

    [Fact]
    public void AnExpenseReceipt_ShowsNoDiscountYet()
    {
        var checks = ReadingChecks.Check(DocumentKind.ExpenseReceipt, [Read(ReadingFieldName.Discount, "430"), Read(ReadingFieldName.Total, "34744")], Hints());

        Assert.Equal("Total=34744@0.9", Shown(checks.Kept));
    }

    [Theory]
    [InlineData("EUR", "HUF", 0.5)] // the receipt's currency counts: two cents off is a misread in euros
    [InlineData(null, "HUF", 0.9)] // without one, the hint: nothing in forints, which have no cents
    [InlineData(null, null, 0.5)]
    public void HowCloseIsClose_DependsOnTheCurrency_TheReceiptsOrElseTheHint(string? read, string? hint, double volumeConfidence)
    {
        // 0.5 l at 2.04 makes 1.02 against a total of 1.00
        List<RecognizedValue> values = [Read(ReadingFieldName.Total, "1.00"), Read(ReadingFieldName.Volume, "0.5"), Read(ReadingFieldName.UnitPrice, "2.04")];
        if (read is not null) values.Add(Read(ReadingFieldName.Currency, read));

        var kept = Apply(DocumentKind.FuelReceipt, values, Hints(currency: hint));

        Assert.Equal(volumeConfidence, kept.Single(v => v.Name == ReadingFieldName.Volume).Confidence);
    }

    [Theory]
    [InlineData("200100", 0.3, 0.3)]
    [InlineData("260000", 0.3, 0.3)] // doubted by the checks, and the model's 0.3 is less still
    [InlineData("200100", 95, 0.95)] // a model that rates in per cent
    [InlineData("200100", 150, ReadingChecks.Doubtful)] // no scale makes sense of it
    [InlineData("200100", -1, ReadingChecks.Doubtful)]
    [InlineData("200100", double.NaN, ReadingChecks.Doubtful)]
    public void TheModelsOwnRating_OnlyEverLowersTheResult_AndIsReadOnTheScaleItWasGivenOn(string odometer, double rated, double confidence)
    {
        var kept = Apply(DocumentKind.Odometer, [Read(ReadingFieldName.Odometer, odometer, rated)], Hints());

        Assert.Equal(confidence, kept.Single().Confidence);
    }

    [Theory]
    [InlineData(0.3, "")] // a low rating is the model's own word, not a missing one
    [InlineData(95, "")]
    [InlineData(150, "Odometer:NoConfidence")]
    [InlineData(-1, "Odometer:NoConfidence")]
    [InlineData(double.NaN, "Odometer:NoConfidence")] // none given
    public void ARatingOnNoScale_IsSaidToBeNone(double rated, string issues) =>
        Assert.Equal(issues, Why(DocumentKind.Odometer, [Read(ReadingFieldName.Odometer, "200100", rated)], Hints()));
}
