using System.Text.Json;

namespace GymWebApi.Clubhouse;
public record Club(string Id, string Name, string Address, double Lat, double Lng, string Hours, string About, string[] Facilities);
public record Trainer(string Id, string Name, string ClubId, string Specialty);
public record ClassRule(string Id, string Name, string TrainerId, int[] Days, string Time, int Minutes, int Capacity, bool Personal, string Description);
public record GymEvent(string Name, string Place, int Weekday, int WeekOfMonth, string Time, string Description);
public record Plan(string Id, string Name, string Description, int[] MonthlyRates, bool GroupAccess, int Credits);
public record Catalog(Club[] Clubs, Trainer[] Trainers, ClassRule[] Classes, GymEvent[] Events, Plan[] Plans)
{
    static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Sofia");
    public static DateTime Now => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Zone);
    public static DateOnly Today => DateOnly.FromDateTime(Now);

    public static Catalog Load(string root) => JsonSerializer.Deserialize<Catalog>(File.ReadAllText(Path.Combine(root, "Clubhouse", "catalog.json")), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    public IEnumerable<Session> Sessions(DateOnly start, DateOnly end)
    {
        for (var day = start; day < end; day = day.AddDays(1))
            foreach (var rule in Classes.Where(r => r.Days.Contains((int)day.DayOfWeek)).OrderBy(r => r.Time))
            {
                var trainer = Trainers.First(t => t.Id == rule.TrainerId);
                yield return new Session($"{day:yyyy-MM-dd}_{rule.Id}", day, rule, trainer);
            }
    }

    public Session? Find(string id) => id.Length > 11 && DateOnly.TryParseExact(id[..10], "yyyy-MM-dd", out var day) && day >= Today.AddYears(-2) && day <= Today.AddYears(1) ? Sessions(day, day.AddDays(1)).FirstOrDefault(x => x.Id == id) : null;
    public object[] UpcomingEvents() => Enumerable.Range(0, 100).Select(i => Today.AddDays(i)).SelectMany(d => Events.Where(e => (int)d.DayOfWeek == e.Weekday && (d.Day - 1) / 7 + 1 == e.WeekOfMonth).Select(e => new { e.Name, e.Place, date = d, e.Time, e.Description })).Cast<object>().ToArray();
}

public record Session(string Id, DateOnly Date, ClassRule Rule, Trainer Trainer)
{
    public bool Started => Date.ToDateTime(TimeOnly.Parse(Rule.Time)) <= Catalog.Now;
}

public record Price(int Months, int BaseCents, int TermCents, int TotalCents, int EligibilityPercent);
public static class Pricing
{
    public static int Eligibility(Member? member)
    {
        if (member?.BirthDate is DateOnly dob && (dob.AddYears(18) > Catalog.Today || dob.AddYears(65) <= Catalog.Today))
            return 20;
        return member?.Student == true ? 10 : 0;
    }

    public static Price? Quote(Plan plan, int months, int discount)
    {
        int index = Array.IndexOf(new[] { 1, 3, 12 }, months);
        if (index < 0 || (plan.Credits > 0 && months != 1))
            return null;
        int factor = plan.Credits > 0 ? 1 : months;
        int term = plan.MonthlyRates[index] * 100 * factor;
        int applied = plan.Credits > 0 ? 0 : discount;
        return new(months, plan.MonthlyRates[0] * 100 * factor, term, term * (100 - applied) / 100, applied);
    }
}
