using System.Text;
using System.Text.Json.Nodes;
using Tankstat.Application.Recognition;
using Tankstat.Domain.Recognition;

namespace Tankstat.Infrastructure.Recognition;

/// <summary>
/// What a model is told. The system prompt (how to read a dashboard or a receipt) is the app's own unless the operator replaces it; the
/// contract (what this photo may show and the exact shape of the answer) always goes with the photo, so a custom prompt changes how the
/// model reads, never what the app gets back. Hints such as the vehicle's latest reading are deliberately not part of it: a model given a
/// number tends to answer with it.
/// </summary>
internal static class OpenAiCompatiblePrompt
{
    /// <summary>The reading guidance the app ships (docs/photo-reading.md prints it, so an operator's own prompt can start from it).</summary>
    public const string DefaultSystemPrompt = """
        You read photos for a vehicle fuel log and answer in JSON only. A photo shows one of two things: a car's instrument cluster, or a receipt.

        Dashboards. Read the odometer: the total distance the car has driven, a whole number of 4 to 7 digits, usually labelled km or mi (or ODO) and shown on the small display between the gauges or at the bottom of the cluster. It is not the trip meter (a smaller number with one decimal, labelled trip, A or B), not a service countdown ("Oil change and inspection in 10100 km", "4800 km múlva"), not a distance or consumption since start, not the clock, the date, the outside temperature, the speed or the fuel range. If the display shows no total distance, say the photo shows no odometer. Many displays use seven-segment digits: read each digit on its own and watch the pairs that look alike (0 and 8, 6 and 8, 1 and 7, 5 and 6, 3 and 9). Give the number exactly as printed, without spaces or units.

        Fuel receipts. The total is the amount paid for the fuel itself: on a receipt with nothing but fuel, the final amount after discounts (labelled TOTAL, Összesen, Fizetendő, Summe or Gesamt); on one that also lists other items (a car wash, screen wash, snacks), the fuel line's own amount after the discount printed for it, never the receipt's grand total. Then read the volume of fuel (litres or gallons, usually with 2 or 3 decimals next to the fuel's name: Diesel, 95, E10, benzin, gázolaj, Super), the price per litre as printed, the currency and the date of purchase. Volume times unit price equals the total unless a discount is printed for the fuel; if they differ and none is printed, re-read them before answering.

        Other receipts. Read the total paid, the currency, the date and the shop's name as the title (the name printed at the top, not its address or tax number).

        Rules. Report only what you can actually read on the photo. Leave a value out rather than guess, and never invent a value that is not printed. Rate each value with a confidence between 0 and 1: 1 when it is clearly legible and unambiguous, about 0.8 when it is readable but small or partly blurred, under 0.6 when you had to guess. Read numbers and dates in the conventions of the receipt's language (a comma may be the decimal separator) and convert them to the output format you are asked for.
        """;

    /// <summary>How numbers and dates are written where the user works (the locale the dialog sends), for the model to undo.</summary>
    private static readonly Dictionary<string, string> Conventions = new()
    {
        ["hu"] = "The receipt is probably Hungarian: numbers use a comma as the decimal separator and a space or a dot between thousands (1 234,5 or 1.234,5), and dates are written year first (2026.10.05.).",
        ["de"] = "The receipt is probably German: numbers use a comma as the decimal separator and a dot between thousands (1.234,5), and dates are written day first (05.10.2026).",
        ["en"] = "The receipt is probably in English: numbers use a dot as the decimal separator and a comma between thousands (1,234.5); a date may be day/month/year or month/day/year, decide from the other clues on the receipt.",
    };

    /// <summary>The user message next to the photo: what it may show, the exact shape and formats of the answer, the local conventions.</summary>
    public static string Contract(IReadOnlySet<DocumentKind> kinds, string locale)
    {
        var allowed = Allowed(kinds);
        var entry = allowed.Contains(DocumentKind.FuelReceipt) ? "a refuelling entry" : allowed.Contains(DocumentKind.ExpenseReceipt) ? "an expense entry" : "a log entry";
        var fields = string.Join(" ", allowed.Select(k => $"{RecognitionNames.Kinds[k]}: {string.Join(", ", ReadingChecks.FieldsOf(k).Select(RecognitionNames.Name))}."));
        var text = new StringBuilder()
            .Append($"This photo was taken for {entry}, so it shows {string.Join(", ", allowed.Select(Described))} or neither (\"unknown\"). ")
            .Append("Answer with JSON only, in exactly this shape: {\"kind\": \"...\", \"fields\": [{\"name\": \"...\", \"value\": \"...\", \"confidence\": 0.0}]}. ")
            .Append($"The fields of each kind: {fields} Leave out a field you cannot read; with \"unknown\" there are no fields. ")
            .Append("Formats: odometer as digits only, without unit or separators; total, volume and unitPrice with \".\" as the decimal separator, no thousands separators, no currency sign; ")
            .Append("currency as an ISO 4217 code (HUF, EUR, USD); date as yyyy-MM-dd; title as the shop's name, at most 120 characters. Every value is a string.");
        if (Conventions.TryGetValue(locale, out var convention)) text.Append(' ').Append(convention).Append(" Convert them to the formats above.");
        return text.ToString();
    }

    /// <summary>
    /// The shape of the answer for <c>response_format: json_schema</c>: the allowed kinds and their fields as enums, every property required and
    /// nothing else allowed (what strict mode wants), and no bounds or patterns, which not every server supports.
    /// </summary>
    public static JsonObject Schema(IReadOnlySet<DocumentKind> kinds)
    {
        var allowed = Allowed(kinds);
        var kindNames = new JsonArray([.. allowed.Select(k => (JsonNode?)RecognitionNames.Kinds[k]), "unknown"]);
        var fieldNames = new JsonArray([.. allowed.SelectMany(ReadingChecks.FieldsOf).Distinct().Select(f => (JsonNode?)RecognitionNames.Name(f))]);
        return new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("kind", "fields"),
            ["properties"] = new JsonObject
            {
                ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = kindNames },
                ["fields"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = false,
                        ["required"] = new JsonArray("name", "value", "confidence"),
                        ["properties"] = new JsonObject
                        {
                            ["name"] = new JsonObject { ["type"] = "string", ["enum"] = fieldNames },
                            ["value"] = new JsonObject { ["type"] = "string" },
                            ["confidence"] = new JsonObject { ["type"] = "number" },
                        },
                    },
                },
            },
        };
    }

    private static List<DocumentKind> Allowed(IReadOnlySet<DocumentKind> kinds) =>
        kinds.Where(RecognitionNames.Kinds.ContainsKey).OrderBy(k => k).ToList();

    private static string Described(DocumentKind kind) => kind switch
    {
        DocumentKind.Odometer => "an odometer (\"odometer\")",
        DocumentKind.FuelReceipt => "a fuel receipt (\"fuel-receipt\")",
        DocumentKind.ExpenseReceipt => "a receipt (\"expense-receipt\")",
        _ => $"\"{RecognitionNames.Kinds[kind]}\"",
    };
}
