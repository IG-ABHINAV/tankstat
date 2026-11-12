using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tankstat.Domain.Tyres;

namespace Tankstat.Domain.UnitTests;

/// <summary>
/// The rules of a vehicle's tyre history, from <c>contracts/tyre-swaps/tyre-swaps.json</c>: the same file the frontend's
/// <c>TyreHistory.unit</c> reads over the device's ports (<c>src/tyres/tyreHistory.ts</c>, <c>src/tyres/tyreLog.ts</c>), so a tyre change
/// made offline shows as the server will make it (#287), the tyre log's lines included (#288).
/// </summary>
public class TyreSwapContractTests
{
    private static readonly JsonElement Fixture =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "tyre-swaps", "tyre-swaps.json"))).RootElement;

    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Car = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var c in Fixture.GetProperty("cases").EnumerateArray()) data.Add(c.GetProperty("name").GetString()!);
        return data;
    }

    /// <summary>The contract's short ids as GUIDs, the same one for the same name.</summary>
    private static Guid IdOf(string name) => new(MD5.HashData(Encoding.UTF8.GetBytes(name)));

    private static DateOnly Day(JsonElement e) => DateOnly.Parse(e.GetString()!);
    private static DateOnly? DayOrNull(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : Day(e);
    private static bool? Pairs(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e.GetBoolean();

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheServerFollowsTheContract(string name)
    {
        var c = Fixture.GetProperty("cases").EnumerateArray().Single(x => x.GetProperty("name").GetString() == name);
        var names = new Dictionary<Guid, string>();
        var sets = c.GetProperty("sets").EnumerateArray().Select(s =>
        {
            var id = s.GetProperty("id").GetString()!;
            var set = TyreSet.Create(Owner, Owner, Car, s.GetProperty("name").GetString()!, TyreSeason.Summer, null, new DateOnly(2020, 1, 1), null, null, 0, null, IdOf(id));
            if (DayOrNull(s.GetProperty("retiredOn")) is { } retired) set.Retire(retired);
            return set;
        }).ToList();
        var installations = c.GetProperty("installations").EnumerateArray().Select(s =>
        {
            names[IdOf(s.GetProperty("id").GetString()!)] = s.GetProperty("id").GetString()!;
            return TyreInstallation.Create(Owner, Owner, Car, IdOf(s.GetProperty("set").GetString()!), Day(s.GetProperty("from")), DayOrNull(s.GetProperty("until")),
                Pairs(s.GetProperty("pairOneFront")), IdOf(s.GetProperty("id").GetString()!));
        }).ToList();
        names[IdOf("new")] = "new";
        var history = new TyreHistory(Owner, Car, sets, installations);
        // Every installation has its line before the change (#288), with the notes the case gives them.
        var lines = installations.Select(i => TyreLog.LineFor(i, history, Owner)).ToList();
        if (c.TryGetProperty("lineNotes", out var lineNotes))
            foreach (var note in lineNotes.EnumerateObject())
            {
                var line = lines.Single(l => l.Id == IdOf(note.Name));
                line.Update(line.Kind, line.SetId, line.Day, null, note.Value.GetString());
            }
        TyreChanges changes = new();
        TyreSet SetOf(JsonElement op) => sets.Single(s => s.Id == IdOf(op.GetProperty("set").GetString()!));

        void Run()
        {
            switch (c.GetProperty("op").GetString())
            {
                case "swap":
                    var swap = c.GetProperty("swap");
                    changes = history.Swap(SetOf(swap), Day(swap.GetProperty("day")), Pairs(swap.GetProperty("pairOneFront")), swap.GetProperty("replace").GetBoolean(), Owner, Now,
                        IdOf("new"), swap.GetProperty("pairsGiven").GetBoolean());
                    break;
                case "positions":
                    var positions = c.GetProperty("positions");
                    changes = history.ChangePositions(SetOf(positions), Day(positions.GetProperty("day")), Pairs(positions.GetProperty("pairOneFront")), Owner, IdOf("new"));
                    break;
                case "move":
                    var move = c.GetProperty("move");
                    changes = history.MoveSwap(installations.Single(s => s.Id == IdOf(move.GetProperty("installation").GetString()!)), Day(move.GetProperty("day")), Owner);
                    break;
                default:
                    var moving = c.TryGetProperty("moving", out var ids) ? ids.EnumerateArray().Select(m => installations.Single(s => s.Id == IdOf(m.GetString()!))).ToList() : null;
                    history.Check(moving);
                    break;
            }
        }

        if (c.TryGetProperty("error", out var error))
        {
            var thrown = Assert.Throws<DomainException>(Run);
            Assert.Equal(error.GetProperty("key").GetString(), thrown.Key);
            var expected = error.GetProperty("args").EnumerateObject().ToDictionary(a => a.Name, a => a.Value.ValueKind == JsonValueKind.Null ? null : a.Value.GetString());
            Assert.Equal(expected, thrown.Args.ToDictionary(a => a.Key, a => a.Value?.ToString()));
            return;
        }
        Run();
        var after = history.Live.OrderBy(s => s.From)
            .Select(s => $"{names[s.Id]} {sets.Single(x => x.Id == s.SetId).Name} {s.From:yyyy-MM-dd}..{s.Until:yyyy-MM-dd} {s.PairOneFront}");
        var want = c.GetProperty("expect").EnumerateArray()
            .Select(s => $"{s.GetProperty("id").GetString()} {sets.Single(x => x.Id == IdOf(s.GetProperty("set").GetString()!)).Name} {Day(s.GetProperty("from")):yyyy-MM-dd}..{DayOrNull(s.GetProperty("until")):yyyy-MM-dd} {Pairs(s.GetProperty("pairOneFront"))}");
        Assert.Equal(want, after);
        if (!c.TryGetProperty("lines", out var expectedLines)) return;

        lines.AddRange(TyreLog.Follow(changes, history, lines, Owner, Now).Created);
        string Name(TyreEntry line) => names.GetValueOrDefault(line.Id, "kept");
        string SetName(Guid? id) => sets.Single(x => x.Id == id).Name;
        Assert.Equal(
            expectedLines.EnumerateArray().Select(l =>
                $"{l.GetProperty("id").GetString()} {l.GetProperty("kind").GetString()} {SetName(IdOf(l.GetProperty("set").GetString()!))} {l.GetProperty("day").GetString()} {l.GetProperty("note").GetString()}"),
            lines.Where(l => !l.IsDeleted).OrderBy(l => l.Day).ThenBy(Name).Select(l => $"{Name(l)} {l.Kind} {SetName(l.SetId)} {l.Day:yyyy-MM-dd} {l.Note}"));
        Assert.Equal(c.GetProperty("trashedLines").EnumerateArray().Select(t => t.GetString()), lines.Where(l => l.IsDeleted).Select(Name).Order());
    }
}
