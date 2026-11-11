using Tankstat.Application.Expenses;
using Tankstat.Application.Vehicles;
using Tankstat.Domain.Measurements;
using Tankstat.Domain.Notifications;
using Tankstat.Domain.Odometers;
using Tankstat.Domain.Recurring;
using Tankstat.Domain.Tyres;
using Tankstat.Domain.Vehicles;

namespace Tankstat.Seeder;

/// <param name="Expenses">The visits of its schedules and the one-off expenses, in date order.</param>
/// <param name="Completions">The links from each schedule to the expenses of its visits, as marking it done leaves them.</param>
/// <param name="TyreSets">A summer and a winter set (#287), with <paramref name="TyreStretches"/>: their time on the car.</param>
public sealed record GeneratedVehicle(
    Vehicle Vehicle, IReadOnlyList<Refueling> Refuelings, IReadOnlyList<RecurringExpense> Recurring, IReadOnlyList<Expense> Expenses,
    IReadOnlyList<RecurringCompletion> Completions, IReadOnlyList<TyreSet>? TyreSets = null, IReadOnlyList<TyreStretch>? TyreStretches = null);

/// <summary>
/// Creates random but internally consistent test data through the domain's own factories (so every rule of the
/// real application applies). Deterministic for a given seed and date. Everything is owned by the anonymous owner
/// used when authentication is off; no users are created.
/// </summary>
public sealed class DataGenerator(TimeProvider clock)
{
    /// <summary>The owner used by the application when authentication is off.</summary>
    public static readonly Guid NoAuthOwner = Guid.Empty;

    private static readonly string[] Models =
    [
        "Skoda Octavia", "Skoda Fabia", "Skoda Superb", "VW Golf", "VW Passat", "VW Polo", "Opel Astra", "Opel Corsa",
        "Ford Focus", "Ford Fiesta", "Toyota Corolla", "Toyota Yaris", "Suzuki Swift", "Suzuki Vitara", "Renault Clio",
        "Renault Megane", "Peugeot 208", "Peugeot 308", "Honda Civic", "Mazda 3", "Hyundai i30", "Kia Ceed", "Fiat Panda",
        "Dacia Duster", "Dacia Sandero", "Seat Leon", "BMW 320d", "Audi A4", "Mercedes C200", "Volvo V60",
    ];

    private static readonly string[] Tags = ["family", "work", "weekend", "city", "old", "spare", "company", "daily"];

    public IEnumerable<GeneratedVehicle> Generate(SeedOptions options)
    {
        var random = new Random(options.RandomSeed);
        var scheduleRandom = new Random(options.RandomSeed + 1); // its own sequence, so the vehicles and logs of a seed stay as they were
        var expenseRandom = new Random(options.RandomSeed + 3); // and its own too (+2 is the notifications'), so expenses move nothing else
        var discountRandom = new Random(options.RandomSeed + 4); // the discounts on fill-ups: their own sequence, so the logs of a seed stay as they were
        var tyreRandom = new Random(options.RandomSeed + 5); // the tyres: their own sequence too
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var total = options.Vehicles + options.Trashed;

        for (var i = 0; i < total; i++)
        {
            var fuel = PickFuel(random);
            var market = PickMarket(random);
            var vehicle = Vehicle.Create(NoAuthOwner, UniqueName(random, names), random.NextDouble() < 0.1 ? null : Plate(random), fuel, market.Units);

            if (i >= options.Vehicles) vehicle.MarkDeleted(clock.GetUtcNow() - TimeSpan.FromDays(random.Next(1, 61)) - TimeSpan.FromMinutes(random.Next(0, 1440)), vehicle.OwnerId);

            var count = random.Next(options.RefuelingsPerVehicle.Min, options.RefuelingsPerVehicle.Max + 1);
            var log = Refuelings(random, discountRandom, vehicle, market, count, today);
            var trajectory = OdometerTrajectory.Of(log, DefaultKmPerDay * market.DistancePerKm);
            var schedules = vehicle.IsDeleted ? [] : Recurring(scheduleRandom, vehicle, market, trajectory, options.RecurringPerVehicle, today);
            var (expenses, completions) = Expenses(expenseRandom, vehicle, market, trajectory, schedules, options.ExpensesPerVehicle, today);
            var (sets, stretches) = vehicle.IsDeleted || log.Count == 0 ? ([], []) : Tyres(tyreRandom, vehicle, market, log.Min(r => r.Date), today);
            yield return new GeneratedVehicle(vehicle, log, schedules, expenses, completions, sets, stretches);
        }
    }

    private const double KmToMiles = 0.621371;
    private const double LitersPerUsGallon = 3.78541;
    private const double LitersPerImperialGallon = 4.54609;

    /// <summary>The pace of a vehicle whose one refuelling says nothing about how far it goes a day.</summary>
    private const double DefaultKmPerDay = 40;

    /// <summary>How often a fill-up was not logged (the next one says so).</summary>
    private const double MissedFillUpShare = 0.03;

    /// <summary>Where a vehicle "lives": its units, the currency it is normally paid in (and one for the odd trip abroad) and a price level.</summary>
    private sealed record Market(MeasurementUnits Units, string HomeCurrency, double HomeRate, string AbroadCurrency, double AbroadRate)
    {
        public double LitersPerUnit => Units.Volume switch { VolumeUnit.UsGallons => LitersPerUsGallon, VolumeUnit.ImperialGallons => LitersPerImperialGallon, _ => 1 };
        public double DistancePerKm => Units.Distance == DistanceUnit.Miles ? KmToMiles : 1;
    }

    // Prices are generated in euro per litre and multiplied by the rate of the currency they are paid in.
    private static readonly Market[] Markets =
    [
        new(MeasurementUnits.Metric, "EUR", 1.0, "HUF", 400),
        new(MeasurementUnits.Metric, "HUF", 400, "EUR", 1.0),
        new(MeasurementUnits.Create(DistanceUnit.Miles, VolumeUnit.ImperialGallons), "GBP", 0.85, "EUR", 1.0),
        new(MeasurementUnits.Create(DistanceUnit.Miles, VolumeUnit.UsGallons), "USD", 1.1, "EUR", 1.0),
    ];

    private static Market PickMarket(Random random)
    {
        var roll = random.NextDouble();
        return roll < 0.45 ? Markets[0] : roll < 0.75 ? Markets[1] : roll < 0.88 ? Markets[2] : Markets[3];
    }

    private static FuelType PickFuel(Random random)
    {
        var roll = random.NextDouble();
        return roll < 0.5 ? FuelType.Petrol : roll < 0.9 ? FuelType.Diesel : FuelType.Lpg;
    }

    private static string UniqueName(Random random, HashSet<string> taken)
    {
        var baseName = $"{Models[random.Next(Models.Length)]} ({Tags[random.Next(Tags.Length)]})";
        var name = baseName;
        for (var n = 2; !taken.Add(name); n++) name = $"{baseName} {n}";
        return name;
    }

    private static string Plate(Random random) =>
        $"{(char)('A' + random.Next(26))}{(char)('A' + random.Next(26))}{(char)('A' + random.Next(26))}-{random.Next(100, 1000)}";

    /// <summary>
    /// A fuel log walked backwards in time from today: the dates only increase, the odometer only increases, and the litres of
    /// each fill-up equal the distance driven since the previous one at the vehicle's consumption (give or take 10%). Now and then a
    /// fill-up was not logged: the odometer runs on by its distance and the next log is marked (<see cref="Refueling.MissedPreviousFillUp"/>).
    /// </summary>
    private static List<Refueling> Refuelings(Random random, Random discountRandom, Vehicle vehicle, Market market, int count, DateOnly today)
    {
        var log = new List<Refueling>(count);
        if (count == 0) return log;

        // Consumption (litres per 100 km), tank size (litres) and price (euro per litre) depend on the fuel.
        var (consumption, tank, pricePerLiter) = vehicle.FuelType switch
        {
            FuelType.Diesel => (4.5 + random.NextDouble() * 3.0, 40 + random.Next(0, 31), 1.50 + random.NextDouble() * 0.2),
            FuelType.Lpg => (8.0 + random.NextDouble() * 4.0, 35 + random.Next(0, 26), 0.80 + random.NextDouble() * 0.15),
            _ => (5.5 + random.NextDouble() * 4.0, 35 + random.Next(0, 36), 1.45 + random.NextDouble() * 0.2),
        };

        // Gaps (days) between fill-ups, then dates counted back from a recent last fill-up.
        var gaps = Enumerable.Range(0, count).Select(_ => random.Next(3, 19)).ToArray();
        var date = today.AddDays(-random.Next(0, 8));
        var dates = new DateOnly[count];
        for (var i = count - 1; i >= 0; i--)
        {
            dates[i] = date;
            date = date.AddDays(-gaps[i]);
        }

        // A fill-up: how many litres, and how far they took the car (in the vehicle's unit).
        (double Liters, long Distance) FillUp()
        {
            var liters = tank * (0.5 + random.NextDouble() * 0.45);
            var distanceKm = liters / (consumption * (0.9 + random.NextDouble() * 0.2)) * 100;
            return (liters, Math.Max(1, (long)Math.Round(distanceKm * market.DistancePerKm)));
        }

        var odometer = (long)(random.Next(5_000, 250_000) * market.DistancePerKm);
        for (var i = 0; i < count; i++)
        {
            var missed = i > 0 && random.NextDouble() < MissedFillUpShare;
            if (missed) odometer += FillUp().Distance; // a fill-up nobody logged: only the odometer knows
            var (liters, distance) = FillUp();
            odometer += distance;
            pricePerLiter *= 1 + (random.NextDouble() - 0.5) * 0.02; // the price drifts slowly

            // Mostly paid at home; now and then abroad, in the other currency.
            var abroad = random.NextDouble() < 0.1;
            var currency = abroad ? market.AbroadCurrency : market.HomeCurrency;
            var rate = abroad ? market.AbroadRate : market.HomeRate;

            var volume = Math.Round((decimal)(liters / market.LitersPerUnit), 2);
            var cost = Math.Round((decimal)(liters * pricePerLiter * rate), 2);
            // A fifth of the fill-ups had a loyalty discount of one to three per cent taken off at the till: the cost is what was paid.
            var discount = discountRandom.NextDouble() < 0.2 ? Math.Round(cost * (decimal)(0.01 + discountRandom.NextDouble() * 0.02), 2) : (decimal?)null;

            log.Add(Refueling.Create(
                vehicle.OwnerId, vehicle.OwnerId, vehicle.Id, dates[i], volume,
                Cost.Create(vehicle.OwnerId, vehicle.Id, dates[i], cost, currency, discount),
                OdometerReading.Create(vehicle.OwnerId, vehicle.Id, dates[i], odometer),
                isFullTank: random.NextDouble() < 0.85, missedPreviousFillUp: missed));
        }
        ConsumptionCalculator.Apply(log); // stored like the app stores it
        return log;
    }

    // ---- Tyres ------------------------------------------------------------------------------------------------------

    private static readonly string[] SummerTyres = ["Michelin Primacy 4", "Continental PremiumContact 6", "Bridgestone Turanza 6", "Goodyear EfficientGrip Performance 2"];
    private static readonly string[] WinterTyres = ["Continental WinterContact TS 870", "Michelin Alpin 6", "Nokian Snowproof 2", "Bridgestone Blizzak LM005"];
    private static readonly string[] TyreSizes = ["195/65 R15 91H", "205/55 R16 91V", "225/45 R17 94W"];

    /// <summary>
    /// A summer and a winter set (#287): each used before the log starts (a rough figure for that time, standing until the first swap), swapped
    /// through the history's own rules (<see cref="TyreHistory"/>) around the start of April and of November from the log's first day to today,
    /// each set's pairs exchanged front to rear every other season. No reading is typed: the distances come from the log's readings.
    /// </summary>
    private (List<TyreSet> Sets, List<TyreStretch> Stretches) Tyres(Random random, Vehicle vehicle, Market market, DateOnly first, DateOnly today)
    {
        var owner = vehicle.OwnerId;
        var size = TyreSizes[random.Next(TyreSizes.Length)];
        TyreSet Make(string[] names, TyreSeason season)
        {
            var since = new DateOnly(first.Year, first.Month, 1).AddMonths(-random.Next(0, 25));
            var made = since.AddDays(-random.Next(30, 300));
            var week = Math.Clamp(made.DayOfYear / 7 + 1, 1, 52);
            var rough = (long)Math.Round(random.Next(0, 30) * 1000 * market.DistancePerKm);
            return TyreSet.Create(owner, owner, vehicle.Id, names[random.Next(names.Length)], season, size, since, week, made.Year, rough, first);
        }
        var summer = Make(SummerTyres, TyreSeason.Summer);
        var winter = Make(WinterTyres, TyreSeason.Winter);
        var history = new TyreHistory(owner, vehicle.Id, [summer, winter], []);
        var now = clock.GetUtcNow();

        bool IsSummer(DateOnly day) => day.Month is >= 4 and <= 10;
        var front = new Dictionary<Guid, bool> { [summer.Id] = true, [winter.Id] = true };
        TyreSet? on = null;
        void Fit(TyreSet set, DateOnly day)
        {
            if (set == on) return; // the log began in that season: the set is on already
            history.Swap(set, day, front[set.Id], replace: false, owner, now);
            front[set.Id] = !front[set.Id]; // exchanged front to rear when it goes on again next year
            on = set;
        }
        Fit(IsSummer(first) ? summer : winter, first);
        for (var year = first.Year; year <= today.Year; year++)
            foreach (var (month, set) in new[] { (4, summer), (11, winter) })
            {
                var day = new DateOnly(year, month, 1).AddDays(random.Next(-10, 15));
                if (day > first && day <= today) Fit(set, day);
            }
        return ([summer, winter], [.. history.Live]);
    }

    // ---- Recurring expenses -----------------------------------------------------------------------------------------

    /// <param name="MinEuro">The cheapest visit, in euro (multiplied by the rate of the vehicle's currency).</param>
    private sealed record Template(string Title, string Category, RecurrenceKind Kind, int? Months, long? DistanceKm, double MinEuro, double MaxEuro);

    private static readonly Template[] Templates =
    [
        new("Insurance", "Insurance", RecurrenceKind.Time, 12, null, 300, 900),
        new("Vignette", "Fees", RecurrenceKind.Time, 12, null, 40, 60),
        new("Technical inspection", "Service", RecurrenceKind.Time, 24, null, 40, 90),
        new("Oil change", "Service", RecurrenceKind.Combined, 12, 15_000, 80, 200),
        new("Tyres", "Service", RecurrenceKind.Odometer, null, 40_000, 300, 700),
    ];

    private enum Due { Overdue, Soon, Upcoming }

    /// <summary>
    /// Schedules with the instance's default warnings, last done so that some are overdue, some due soon and the rest upcoming (the app
    /// works out their reminders when the notifications are looked at). Ones that count distance need a reading, so a vehicle without a
    /// fuel log only gets time-based ones. A schedule was last done where the vehicle's log puts it (<see cref="OdometerTrajectory"/>):
    /// that visit is an expense with a reading, which must fit between the log's readings like any other. Each limit it counts is drawn
    /// for its state: time a day, distance an odometer (on the day the vehicle got there). One that counts both was last done at whichever
    /// of the two came later: that limit stands where its state was drawn and the other one started after its own draw, so it is no
    /// further along, and the app, which goes by the one further along, sees the state drawn. Time-only ones note no odometer.
    /// </summary>
    private List<RecurringExpense> Recurring(Random random, Vehicle vehicle, Market market, OdometerTrajectory? trajectory, IntRange perVehicle, DateOnly today)
    {
        var defaults = new VehicleDefaultsOptions();
        var usable = Templates.Where(t => trajectory is not null || t.Kind == RecurrenceKind.Time).OrderBy(_ => random.Next()).ToList();
        var count = Math.Min(usable.Count, random.Next(perVehicle.Min, perVehicle.Max + 1));

        return usable.Take(count).Select(t =>
        {
            var roll = random.NextDouble();
            var due = roll < 0.25 ? Due.Overdue : roll < 0.5 ? Due.Soon : Due.Upcoming;
            // In the vehicle's unit, rounded to a thousand like a person would set it.
            var distance = t.DistanceKm is { } km ? (long)Math.Round(km * market.DistancePerKm / 1000) * 1000 : (long?)null;

            // Time: due at last done + interval; "soon" is within the 30 warning days.
            DateOnly? byTime = t.Months is { } months
                ? due switch
                {
                    Due.Overdue => today.AddMonths(-months).AddDays(-random.Next(1, 60)),
                    Due.Soon => today.AddMonths(-months).AddDays(random.Next(1, Math.Min(defaults.RecurringWarnDays, 26))),
                    _ => today.AddMonths(-months).AddDays(random.Next(defaults.RecurringWarnDays + 10, months * 28)),
                }
                : null;
            // Distance: due at last done + interval; "soon" is within the warning distance.
            long? byDistance = distance is { } interval && trajectory is { Latest: var current }
                ? due switch
                {
                    Due.Overdue => current - interval - random.Next(100, 2_000),
                    Due.Soon => current - interval + random.Next(50, (int)defaults.RecurringWarnDistance - 50),
                    _ => current - random.Next(0, (int)(interval / 2)),
                }
                : null;
            // A vehicle that has not gone that far yet: the time limit decides, or one that counts only distance was last done at 0.
            if (byDistance is <= 0) byDistance = byTime is null ? 0 : null;
            var (lastDone, lastOdometer) = Later(byTime, byDistance, distance is not null, trajectory);

            return RecurringExpense.Create(
                vehicle.OwnerId, vehicle.OwnerId, vehicle.Id, t.Title, t.Category, null, t.Kind, t.Months, distance, lastDone, lastOdometer,
                defaults.RecurringWarnDays, defaults.RecurringWarnDistance, clock.GetUtcNow());
        }).ToList();
    }

    /// <summary>
    /// The later of the visit a time limit gives (that day, with its odometer when the schedule counts distance) and the one a distance
    /// limit gives (that odometer, on the day the vehicle got there); either may be missing, not both. On a tie the distance's, whose
    /// odometer is then the exact one.
    /// </summary>
    private static (DateOnly Date, long? Odometer) Later(DateOnly? byTime, long? byDistance, bool usesDistance, OdometerTrajectory? trajectory)
    {
        var distanceDate = byDistance is { } value ? trajectory!.DateAt(value) : (DateOnly?)null;
        return byTime is { } timeDate && (distanceDate is null || timeDate > distanceDate)
            ? (timeDate, usesDistance ? trajectory!.At(timeDate) : null)
            : (distanceDate!.Value, byDistance);
    }

    // ---- Expenses ---------------------------------------------------------------------------------------------------

    /// <summary>A kind of one-off expense: its category, what it is called and what it costs in euro.</summary>
    private sealed record OneOff(string Category, string[] Titles, double MinEuro, double MaxEuro, double Share);

    private static readonly OneOff[] OneOffs =
    [
        new("Parking", ["Parking", "Parking garage", "Airport parking", "Street parking"], 2, 25, 0.4),
        new("Washing", ["Car wash", "Hand wash", "Interior cleaning"], 5, 25, 0.25),
        new("Repairs", ["Brake pads", "Wiper blades", "Battery", "Headlight bulb", "Windscreen chip", "Exhaust repair"], 20, 600, 0.2),
        new("Accessories", ["Floor mats", "Phone holder", "First aid kit", "Roof box", "Child seat"], 10, 150, 0.15),
    ];

    /// <summary>How far back the visits of a schedule go (the last one, its baseline, always counts).</summary>
    private const int VisitYears = 3;

    /// <summary>
    /// The vehicle's expenses: every schedule's visits, as marking it done logs them (one expense each, called like the schedule, at the
    /// visit's date and, when the schedule counts distance, odometer; linked to the schedule), the last one at the schedule's baseline and
    /// the earlier ones an interval apart; and <paramref name="oneOffs"/> one-off ones (parking, washing, ...) within the fuel log's dates,
    /// about half of them with the odometer of their day. All in the currency the vehicle is normally paid in. A vehicle without a log
    /// gets its one-off expenses over the last half year, without a reading.
    /// </summary>
    private static (List<Expense> Expenses, List<RecurringCompletion> Completions) Expenses(
        Random random, Vehicle vehicle, Market market, OdometerTrajectory? trajectory, IReadOnlyList<RecurringExpense> schedules, IntRange oneOffs, DateOnly today)
    {
        var expenses = new List<Expense>();
        var completions = new List<RecurringCompletion>();

        Expense Add(DateOnly date, string title, string? category, double euro, long? odometer, string? note = null)
        {
            var expense = ExpenseService.Build(vehicle, vehicle.OwnerId,
                new ExpenseInput(date, title, category, Math.Round((decimal)(euro * market.HomeRate), 2), market.HomeCurrency, odometer, note));
            expenses.Add(expense);
            return expense;
        }

        foreach (var schedule in schedules)
        {
            var template = Templates.First(t => t.Title == schedule.Title);
            var price = template.MinEuro + random.NextDouble() * (template.MaxEuro - template.MinEuro); // the same place each time, give or take
            var (title, category, note) = (RecurringDoneDefaults.Title([schedule]), RecurringDoneDefaults.Category([schedule]), RecurringDoneDefaults.Note([schedule]));
            foreach (var (date, odometer) in Visits(schedule, trajectory, today))
            {
                var expense = Add(date, title, category, price * (0.95 + random.NextDouble() * 0.1), odometer, note);
                completions.Add(RecurringCompletion.Create(expense.Id, schedule.Id));
            }
        }

        var count = random.Next(oneOffs.Min, oneOffs.Max + 1);
        var (from, to) = trajectory is null ? (today.AddDays(-180), today) : (trajectory.First, trajectory.Last);
        for (var i = 0; i < count; i++)
        {
            var kind = PickOneOff(random);
            var date = from.AddDays(random.Next(0, to.DayNumber - from.DayNumber + 1));
            var odometer = trajectory is not null && random.NextDouble() < 0.5 ? trajectory.At(date) : (long?)null;
            Add(date, kind.Titles[random.Next(kind.Titles.Length)], kind.Category, kind.MinEuro + random.NextDouble() * (kind.MaxEuro - kind.MinEuro), odometer);
        }

        expenses.Sort((a, b) => a.Date.CompareTo(b.Date));
        return (expenses, completions);
    }

    private static OneOff PickOneOff(Random random)
    {
        var roll = random.NextDouble();
        foreach (var kind in OneOffs)
            if ((roll -= kind.Share) < 0) return kind;
        return OneOffs[^1];
    }

    /// <summary>
    /// When a schedule was done, latest first: its baseline, then one interval earlier each time (by months, by distance on the day the
    /// vehicle was there, or for one that counts both whichever came later going back, as the earlier limit decided the visit), for
    /// <see cref="VisitYears"/> years; a distance schedule stops where the odometer would not be above 0.
    /// </summary>
    private static IEnumerable<(DateOnly Date, long? Odometer)> Visits(RecurringExpense schedule, OdometerTrajectory? trajectory, DateOnly today)
    {
        var floor = today.AddYears(-VisitYears);
        var (date, odometer) = (schedule.LastDoneDate, schedule.LastDoneOdometer);
        while (true)
        {
            yield return (date, odometer);

            DateOnly? byTime = schedule.UsesTime ? date.AddMonths(-schedule.IntervalMonths!.Value) : null;
            long? byDistance = schedule.UsesDistance ? odometer!.Value - schedule.IntervalDistance!.Value : null;
            if (byDistance is <= 0) byDistance = null;
            if (byTime is null && byDistance is null) yield break; // nothing before a distance schedule's first kilometre

            (date, odometer) = Later(byTime, byDistance, schedule.UsesDistance, trajectory);
            if (date < floor || odometer is <= 0) yield break;
        }
    }

    // ---- Notifications ----------------------------------------------------------------------------------------------

    private static readonly string[] People = ["Anna Kovács", "Bence Tóth", "Chris Miller", "Dóra Nagy", "Eve Smith", "Frank Weber"];
    private static readonly string[] Levels = ["EDIT", "DELETE", "NONE"];

    /// <summary>
    /// Notifications about access changes for the anonymous user, as the app would have sent them over the last two weeks (made up people,
    /// since the seeder creates no users; the vehicle ones are about the given vehicles): about a third already read, one that stands for
    /// several folded changes and, when there are enough, one "more changes". Deterministic for a given seed.
    /// </summary>
    public IReadOnlyList<Notification> Notifications(SeedOptions options, IReadOnlyList<Vehicle> vehicles)
    {
        var random = new Random(options.RandomSeed + 2);
        var now = clock.GetUtcNow();
        var result = new List<Notification>(options.Notifications);
        for (var i = 0; i < options.Notifications; i++)
        {
            var at = now - TimeSpan.FromMinutes(random.Next(10, 14 * 24 * 60));
            var actor = People[random.Next(People.Length)];
            var other = People[random.Next(People.Length)];
            var otherId = PersonId(other);
            var level = Levels[random.Next(Levels.Length)];
            var vehicle = vehicles.Count > 0 ? vehicles[random.Next(vehicles.Count)] : null;
            var digest = i == options.Notifications - 1 && options.Notifications >= 10;

            Notification n = digest
                ? Create(NotificationKind.MoreActivity, NotificationRef.Instance, null, [], null)
                : (vehicle is null ? random.Next(2, 5) : random.Next(0, 5)) switch
                {
                    0 => Create(NotificationKind.LogAccessChanged, NotificationRef.Vehicle(vehicle!.Id), NotificationRef.Vehicle(vehicle.Id),
                        [("actorName", actor), ("vehicleName", vehicle.Name), ("level", level)], "NONE"),
                    1 => Create(NotificationKind.VehicleShared, NotificationRef.User(otherId), NotificationRef.Vehicle(vehicle!.Id),
                        [("actorName", actor), ("userName", other), ("vehicleName", vehicle.Name), ("level", level)], "NONE"),
                    2 => Create(NotificationKind.DataAccessChanged, NotificationRef.User(otherId), null, [("actorName", actor), ("userName", other), ("level", level == "DELETE" ? "VIEW" : level)], "NONE"),
                    3 => Create(NotificationKind.DataShared, NotificationRef.User(otherId), null, [("actorName", actor), ("userName", other), ("level", level)], "NONE"),
                    _ => Create(NotificationKind.DefaultAccessChanged, NotificationRef.Instance, null, [("actorName", actor), ("level", level == "DELETE" ? "VIEW" : level)], "NONE"),
                };

            // A few stand for several changes made one after the other (the app folds them into one), the digest for many.
            var folded = digest ? random.Next(3, 12) : random.NextDouble() < 0.15 ? random.Next(2, 4) : 1;
            for (var f = 1; f < folded; f++) n.Fold(n.Kind, n.Args, at + TimeSpan.FromMinutes(f * random.Next(1, 30)));
            if (random.NextDouble() < 0.35) n.MarkRead(Min(n.UpdatedAt + TimeSpan.FromMinutes(random.Next(5, 3 * 24 * 60)), now));
            result.Add(n);

            Notification Create(NotificationKind kind, NotificationRef subject, NotificationRef? context, (string Name, string Value)[] args, string? before) =>
                Notification.Create(DataGenerator.NoAuthOwner, kind, subject, context, Guid.NewGuid().ToString("N"), args.ToDictionary(a => a.Name, a => a.Value), before, at);
        }
        return result;
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    /// <summary>A stable made-up id per person, so the notifications about one person are about the same "user".</summary>
    private static Guid PersonId(string name) => new(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(name)));
}
