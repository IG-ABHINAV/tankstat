using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tankstat.Domain.Tyres;

namespace Tankstat.Domain.UnitTests;

/// <summary>
/// The rules of single tyres (#289), from <c>contracts/tyre-places/tyre-places.json</c>: the same file the frontend's <c>TyrePlaces.unit</c>
/// reads over the device's port (<c>src/tyres/tyrePlaces.ts</c>), so a change made offline shows as the server will make it.
/// </summary>
public class TyrePlacesContractTests
{
    private static readonly JsonElement Fixture =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "tyre-places", "tyre-places.json"))).RootElement;

    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Car = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2027, 1, 22, 12, 0, 0, TimeSpan.Zero);

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var c in Fixture.GetProperty("cases").EnumerateArray()) data.Add(c.GetProperty("name").GetString()!);
        return data;
    }

    /// <summary>The contract's short ids as GUIDs, the same one for the same name.</summary>
    private static Guid IdOf(string name) => new(MD5.HashData(Encoding.UTF8.GetBytes(name)));

    private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString()!;
    private static DateOnly Day(JsonElement e) => DateOnly.Parse(e.GetString()!);
    private static DateOnly? DayOrNull(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : Day(e);
    private static TyreCorner? Corner(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : Enum.Parse<TyreCorner>(e.GetString()!);
    private static bool? Pairs(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e.GetBoolean();

    [Fact]
    public void ThePlaceIds_AreWorkedOutAsTheContractPinsThem()
    {
        foreach (var p in Fixture.GetProperty("placeIds").EnumerateArray())
            Assert.Equal(Guid.Parse(Str(p, "place")), TyreIds.PlaceOf(Guid.Parse(Str(p, "tyre")), Guid.Parse(Str(p, "installation"))));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheServerFollowsTheContract(string name)
    {
        var c = Fixture.GetProperty("cases").EnumerateArray().Single(x => Str(x, "name") == name);
        var names = new Dictionary<Guid, string> { [IdOf("new")] = "new" };
        Guid Named(string id)
        {
            names[IdOf(id)] = id;
            return IdOf(id);
        }
        var sets = c.GetProperty("sets").EnumerateArray().Select(s =>
        {
            var set = TyreSet.Create(Owner, Owner, Car, Str(s, "name"), TyreSeason.Winter, null, new DateOnly(2020, 1, 1), null, null, 0, null, Named(Str(s, "id")));
            if (DayOrNull(s.GetProperty("brokenUpOn")) is { } brokenUp) set.BreakUp(brokenUp);
            if (DayOrNull(s.GetProperty("retiredOn")) is { } retired) set.Retire(retired);
            return set;
        }).ToList();
        var installations = c.GetProperty("installations").EnumerateArray().Select(s =>
            TyreInstallation.Create(Owner, Owner, Car, IdOf(Str(s, "set")), Day(s.GetProperty("from")), DayOrNull(s.GetProperty("until")), Pairs(s.GetProperty("pairOneFront")),
                Named(Str(s, "id")))).ToList();
        var tyres = c.GetProperty("tyres").EnumerateArray().Select(t =>
        {
            var tyre = Tyre.Create(Owner, Owner, Car, IdOf(Str(t, "set")), Str(t, "label"), null, new DateOnly(2020, 1, 1), null, null, 0, null, Named(Str(t, "id")));
            if (DayOrNull(t.GetProperty("retiredOn")) is { } retired) tyre.Retire(retired, TyreRetiredWhy.Damaged);
            return tyre;
        }).ToList();
        var places = c.GetProperty("places").EnumerateArray().Select(p =>
            TyrePlace.Create(Owner, Owner, Car, IdOf(Str(p, "tyre")), IdOf(Str(p, "installation")), Day(p.GetProperty("from")), DayOrNull(p.GetProperty("until")),
                Corner(p.GetProperty("corner")), Named(Str(p, "id")))).ToList();
        var history = new TyreHistory(Owner, Car, sets, installations);
        var rules = new TyrePlaces(Owner, Car, history, sets, tyres, places);
        TyreSet SetOf(JsonElement op) => sets.Single(s => s.Id == IdOf(Str(op, "set")));
        IReadOnlyDictionary<Guid, TyreCorner?>? Corners(JsonElement op) =>
            op.GetProperty("corners") is { ValueKind: JsonValueKind.Object } given ? given.EnumerateObject().ToDictionary(x => IdOf(x.Name), x => Corner(x.Value)) : null;
        TyrePlace PlaceOf(JsonElement op) => rules.LivePlaces.Single(p => p.Id == IdOf(Str(op, "place")));

        void Run()
        {
            var op = Str(c, "op");
            var args = op == "check" ? default : c.GetProperty(op);
            switch (op)
            {
                case "breakUp":
                    rules.BreakUp(SetOf(args), [.. args.GetProperty("tyres").EnumerateArray().Select(t => new NewTyre(Named(Str(t, "id")), Str(t, "label"), Corner(t.GetProperty("corner"))))],
                        Day(args.GetProperty("day")), Owner);
                    break;
                case "swap":
                    rules.Swap(SetOf(args), Day(args.GetProperty("day")), null, args.GetProperty("replace").GetBoolean(), Corners(args), Owner, Now, IdOf("new"));
                    break;
                case "positions":
                    rules.ChangePositions(SetOf(args), Day(args.GetProperty("day")), null, Corners(args), Owner, IdOf("new"));
                    break;
                case "move":
                    rules.MoveSwap(installations.Single(s => s.Id == IdOf(Str(args, "installation"))), Day(args.GetProperty("day")), Owner);
                    break;
                case "edit":
                    var installation = installations.Single(s => s.Id == IdOf(Str(args, "installation")));
                    rules.EditInstallation(installation, Day(args.GetProperty("from")), DayOrNull(args.GetProperty("until")), installation.PairOneFront, Owner);
                    break;
                case "add":
                    rules.AddInstallation(SetOf(args), Day(args.GetProperty("from")), DayOrNull(args.GetProperty("until")), null, Owner, IdOf("new"));
                    break;
                case "addPlace":
                    rules.Add(tyres.Single(t => t.Id == IdOf(Str(args, "tyre"))), Day(args.GetProperty("from")), DayOrNull(args.GetProperty("until")), Corner(args.GetProperty("corner")), Owner);
                    break;
                case "editPlace":
                    rules.Edit(PlaceOf(args), Day(args.GetProperty("from")), DayOrNull(args.GetProperty("until")), Corner(args.GetProperty("corner")), Owner);
                    break;
                default:
                    var moving = c.TryGetProperty("moving", out var ids) ? ids.EnumerateArray().Select(m => rules.LivePlaces.Single(p => p.Id == IdOf(m.GetString()!))).ToList() : null;
                    rules.Check(moving, c.TryGetProperty("restoring", out var restoring) && restoring.GetBoolean());
                    break;
            }
        }

        if (c.TryGetProperty("error", out var error))
        {
            var thrown = Assert.Throws<DomainException>(Run);
            Assert.Equal(Str(error, "key"), thrown.Key);
            var expected = error.GetProperty("args").EnumerateObject().ToDictionary(a => a.Name, a => a.Value.ValueKind == JsonValueKind.Null ? null : a.Value.GetString());
            Assert.Equal(expected, thrown.Args.ToDictionary(a => a.Key, a => a.Value?.ToString()));
            return;
        }
        Run();
        var label = rules.Tyres.ToDictionary(t => t.Id, t => names[t.Id]);
        Assert.Equal(
            c.GetProperty("expectPlaces").EnumerateArray().Select(p =>
                $"{Str(p, "tyre")} {Str(p, "installation")} {Str(p, "from")}..{(p.GetProperty("until").ValueKind == JsonValueKind.Null ? "" : Str(p, "until"))} {Corner(p.GetProperty("corner"))}")
                .Order(StringComparer.Ordinal),
            rules.LivePlaces.Select(p => $"{label[p.TyreId]} {names[p.InstallationId]} {p.From:yyyy-MM-dd}..{p.Until:yyyy-MM-dd} {p.Corner}").Order(StringComparer.Ordinal));
        if (c.TryGetProperty("expectRetired", out var retired))
            Assert.Equal(retired.EnumerateArray().Select(r => r.GetString()!).Order(StringComparer.Ordinal),
                rules.Tyres.Where(t => t.IsRetired).Select(t => label[t.Id]).Order(StringComparer.Ordinal));
    }
}
