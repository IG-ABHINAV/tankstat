using System.Text.Json.Nodes;
using Tankstat.Domain.Recognition;
using Tankstat.Infrastructure.Recognition;

namespace Tankstat.Infrastructure.UnitTests;

/// <summary>What a model is told: the contract names every kind and field it may answer with, and the schema is one strict mode accepts.</summary>
public class OpenAiCompatiblePromptTests
{
    private static readonly HashSet<DocumentKind> Refuelling = [DocumentKind.Odometer, DocumentKind.FuelReceipt];
    private static readonly HashSet<DocumentKind> Expense = [DocumentKind.Odometer, DocumentKind.ExpenseReceipt];

    [Fact]
    public void TheContract_NamesWhatThePhotoMayShow_ItsFields_AndHowTheLanguageWritesNumbers()
    {
        var refuelling = OpenAiCompatiblePrompt.Contract(Refuelling, "hu");
        var expense = OpenAiCompatiblePrompt.Contract(Expense, "de");
        var unknownLanguage = OpenAiCompatiblePrompt.Contract(Expense, "fr");

        Assert.Contains("a refuelling entry", refuelling);
        Assert.Contains("fuel-receipt: total, volume, unitPrice, discount, currency, date.", refuelling);
        Assert.DoesNotContain("expense-receipt", refuelling);
        Assert.Contains("Hungarian", refuelling);
        Assert.Contains("an expense entry", expense);
        Assert.Contains("expense-receipt: total, currency, date, title.", expense);
        Assert.Contains("German", expense);
        Assert.DoesNotContain("probably", unknownLanguage); // nothing known about how that language writes
        Assert.Contains("JSON", unknownLanguage); // what OpenAI's json_object mode wants to see in the prompt
    }

    [Fact]
    public void APhotoThatCanOnlyShowTheOdometer_IsForALogEntry_AndOnlyTheOdometerIsOffered()
    {
        var contract = OpenAiCompatiblePrompt.Contract(new HashSet<DocumentKind> { DocumentKind.Odometer, DocumentKind.Unknown }, "fr");

        Assert.Contains("taken for a log entry, so it shows an odometer (\"odometer\") or neither", contract);
        Assert.Contains("odometer: odometer.", contract);
        Assert.DoesNotContain("receipt", contract);
    }

    [Fact]
    public void TheSchema_IsOneStrictModeTakes_EveryPropertyRequiredAndNothingElseAllowed()
    {
        var schema = OpenAiCompatiblePrompt.Schema(Expense);

        var objects = new List<JsonObject>();
        void Walk(JsonNode? node)
        {
            if (node is JsonObject o)
            {
                if (o["type"]?.GetValue<string>() == "object") objects.Add(o);
                foreach (var (_, child) in o) Walk(child);
            }
            else if (node is JsonArray a) foreach (var child in a) Walk(child);
        }
        Walk(schema);

        Assert.Equal(2, objects.Count);
        foreach (var o in objects)
        {
            Assert.False(o["additionalProperties"]!.GetValue<bool>());
            Assert.Equal(o["properties"]!.AsObject().Select(p => p.Key).Order(), o["required"]!.AsArray().Select(r => r!.GetValue<string>()).Order());
        }
        Assert.Equal(["odometer", "expense-receipt", "unknown"], schema["properties"]!["kind"]!["enum"]!.AsArray().Select(k => k!.GetValue<string>()));
        Assert.Equal(["odometer", "total", "currency", "date", "title"], schema["properties"]!["fields"]!["items"]!["properties"]!["name"]!["enum"]!.AsArray().Select(k => k!.GetValue<string>()));
        Assert.DoesNotContain("minimum", schema.ToJsonString()); // bounds and patterns are not portable across servers
    }

    /// <summary>The page that prints what the model is told, relative to the repository root.</summary>
    private const string DocsPage = "docs/photo-reading.md";

    [Fact]
    public void TheDocs_PrintTheBuiltInPrompt_SoAnOperatorsOwnCanStartFromIt()
    {
        var docs = DocsLines();

        Assert.Equal(
            string.Join("\n", OpenAiCompatiblePrompt.DefaultSystemPrompt.ReplaceLineEndings("\n").Split('\n').Select(line => line.Trim())),
            Quoted(docs, "The built-in system prompt is:"));
    }

    [Fact]
    public void TheDocs_PrintTheContractsAndTheSchema_ExactlyAsTheModelGetsThem()
    {
        var docs = DocsLines();

        Assert.Equal(OpenAiCompatiblePrompt.Contract(Refuelling, "hu"), Quoted(docs, "The contract for a photo picked in the **refuelling** dialog, in Hungarian, is:"));
        Assert.Equal(OpenAiCompatiblePrompt.Contract(Expense, "en"), Quoted(docs, "The contract for a photo picked in the **expense** dialogs, in English, is:"));
        var schema = JsonNode.Parse(Fenced(docs, "The schema of `ResponseFormat=JsonSchema`, for the refuelling dialog"));
        Assert.True(JsonNode.DeepEquals(OpenAiCompatiblePrompt.Schema(Refuelling), schema), $"the schema in {DocsPage} is not the one that is sent");
    }

    private static string[] DocsLines() => File.ReadAllLines(Path.Combine(RepoRoot(), DocsPage));

    private static string[] After(string[] docs, string marker)
    {
        var at = Array.FindIndex(docs, line => line.Contains(marker, StringComparison.Ordinal));
        Assert.True(at >= 0, $"{DocsPage} no longer says: {marker}");
        return docs[(at + 1)..];
    }

    /// <summary>The block quote after a line: its text as the paragraph it is.</summary>
    private static string Quoted(string[] docs, string marker) =>
        string.Join("\n", After(docs, marker).SkipWhile(string.IsNullOrWhiteSpace).TakeWhile(line => line.StartsWith('>')).Select(line => line.TrimStart('>').Trim()));

    /// <summary>The code block after a line, without its fences.</summary>
    private static string Fenced(string[] docs, string marker) =>
        string.Join("\n", After(docs, marker).SkipWhile(line => !line.StartsWith("```", StringComparison.Ordinal)).Skip(1).TakeWhile(line => !line.StartsWith("```", StringComparison.Ordinal)));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Tankstat.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Tankstat.slnx not found above the test output.");
    }
}
