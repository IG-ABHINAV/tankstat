using Tankstat.Domain.Recognition;

namespace Tankstat.Infrastructure.Recognition;

/// <summary>The kinds of photo and the values as a model is asked to spell them in its answer.</summary>
internal static class RecognitionNames
{
    public static readonly IReadOnlyDictionary<DocumentKind, string> Kinds = new Dictionary<DocumentKind, string>
    {
        [DocumentKind.Odometer] = "odometer",
        [DocumentKind.FuelReceipt] = "fuel-receipt",
        [DocumentKind.ExpenseReceipt] = "expense-receipt",
    };

    public static readonly IReadOnlyDictionary<string, ReadingFieldName> Fields = new Dictionary<string, ReadingFieldName>
    {
        ["odometer"] = ReadingFieldName.Odometer,
        ["total"] = ReadingFieldName.Total,
        ["volume"] = ReadingFieldName.Volume,
        ["unitPrice"] = ReadingFieldName.UnitPrice,
        ["currency"] = ReadingFieldName.Currency,
        ["date"] = ReadingFieldName.Date,
        ["title"] = ReadingFieldName.Title,
        ["discount"] = ReadingFieldName.Discount,
    };

    /// <summary>The kind a name stands for; anything else (including "unknown") is <see cref="DocumentKind.Unknown"/>.</summary>
    public static DocumentKind Kind(string? name) =>
        Kinds.FirstOrDefault(k => k.Value == name) is { Value: not null } match ? match.Key : DocumentKind.Unknown;

    public static string Name(ReadingFieldName field) => Fields.First(f => f.Value == field).Key;
}
