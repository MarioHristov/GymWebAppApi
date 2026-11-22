using System.Data;
using Microsoft.EntityFrameworkCore;

namespace GymWebApi.Clubhouse;
public record Order(string PlanId, int Months, Guid RequestId, int? ExpectedCents);
public static class Gym
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/clubs", (Catalog c) => new { c.Clubs, c.Trainers, activities = c.Classes.Where(r => !r.Personal).Select(r => new { r.TrainerId, r.Name }) });
        app.MapGet("/api/events", (Catalog c) => c.UpcomingEvents());
        app.MapGet("/api/plans", async (Catalog c, ClubDb db, HttpContext ctx) =>
        {
            var member = ctx.User.Identity?.IsAuthenticated == true ? await db.Members.FindAsync(ctx.MemberId()) : null;
            int discount = Pricing.Eligibility(member);
            return new
            {
                discount,
                plans = c.Plans.Select(p => new { p.Id, p.Name, p.Description, p.GroupAccess, p.Credits, prices = (p.Credits > 0 ? new[] { 1 } : new[] { 1, 3, 12 }).Select(m => Pricing.Quote(p, m, discount)) })
            };
        });
        app.MapPost("/api/enrollments", Enroll).RequireAuthorization();
        app.MapGet("/api/profile", Profile).RequireAuthorization();
        app.MapGet("/api/schedule", Schedule);
        app.MapPut("/api/bookings/{id}", Reserve).RequireAuthorization();
        app.MapDelete("/api/bookings/{id}", Cancel).RequireAuthorization();
    }

    static async Task<IResult> Enroll(Order order, HttpContext ctx, Catalog c, ClubDb db)
    {
        var plan = c.Plans.FirstOrDefault(p => p.Id == order.PlanId);
        if (plan == null || order.RequestId == Guid.Empty)
            return Accounts.Error("Choose a valid plan.");
        string uid = ctx.MemberId();
        using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var previous = await db.Enrollments.SingleOrDefaultAsync(e => e.MemberId == uid && e.RequestId == order.RequestId);
        if (previous != null)
            return previous.Plan == plan.Name && previous.Months == order.Months ? Results.Ok(previous) : Accounts.Error("Order reference already used.", 409);
        var member = await db.Members.FindAsync(uid);
        var price = Pricing.Quote(plan, order.Months, Pricing.Eligibility(member));
        if (price == null)
            return Accounts.Error("Choose an available term.");
        if (order.ExpectedCents.HasValue && order.ExpectedCents != price.TotalCents)
            return Accounts.Error("Your price changed. Reload the plans before confirming.", 409);
        var start = Catalog.Today;
        if (plan.Credits == 0)
        {
            var end = await db.Enrollments.Where(x => x.MemberId == uid && x.TrainingCredits == 0).MaxAsync(x => (DateOnly? )x.End);
            if (end > start)
                start = end.Value;
        }

        var enrollment = new Enrollment
        {
            MemberId = uid,
            Plan = plan.Name,
            Start = start,
            End = start.AddMonths(plan.Credits > 0 ? 12 : order.Months),
            Months = order.Months,
            TotalCents = price.TotalCents,
            GroupAccess = plan.GroupAccess,
            TrainingCredits = plan.Credits,
            DiscountPercent = price.EligibilityPercent,
            RequestId = order.RequestId
        };
        db.Add(enrollment);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return Results.Ok(enrollment);
    }

    static async Task<IResult> Profile(ClubDb db, AccountDb accounts, HttpContext ctx, Catalog c)
    {
        var uid = ctx.MemberId();
        var member = await db.Members.AsNoTracking().SingleOrDefaultAsync(x => x.Id == uid) ?? new Member
        {
            Id = uid
        };
        var email = await accounts.Users.Where(x => x.Id == uid).Select(x => x.Email).SingleAsync();
        var history = await db.Enrollments.AsNoTracking().Where(x => x.MemberId == uid).OrderByDescending(x => x.Start).ThenByDescending(x => x.Id).ToListAsync();
        var reservations = await db.Bookings.AsNoTracking().Where(x => x.MemberId == uid).ToListAsync();
        var bookings = reservations.Select(x => c.Find(x.SlotId)).OfType<Session>().Where(x => !x.Started).OrderBy(x => x.Date).ThenBy(x => x.Rule.Time).Select(x => new { x.Id, x.Date, x.Rule.Time, x.Rule.Name, x.Trainer.ClubId });
        return Results.Ok(new { email, member.Name, member.BirthDate, member.Student, today = Catalog.Today, history, bookings, credits = history.Where(x => x.TrainingCredits > 0 && x.End > Catalog.Today).Sum(x => x.TrainingCredits - reservations.Count(b => b.EnrollmentId == x.Id)) });
    }

    static async Task<IResult> Schedule(string month, string? trainer, string? club, ClubDb db, Catalog c, HttpContext ctx)
    {
        if (!DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", out var start) || start < Catalog.Today.AddMonths(-2).AddDays(-31) || start > Catalog.Today.AddMonths(12))
            return Accounts.Error("Choose a month within the schedule window.");
        var end = start.AddMonths(1);
        var counts = await db.Slots.AsNoTracking().Where(x => x.Date >= start && x.Date < end).ToDictionaryAsync(x => x.Id, x => x.Held);
        var mine = ctx.User.Identity?.IsAuthenticated == true ? (await db.Bookings.Where(x => x.MemberId == ctx.MemberId() && x.SlotId.StartsWith(month)).Select(x => x.SlotId).ToListAsync()).ToHashSet() : new HashSet<string>();
        var sessions = c.Sessions(start, end).Where(x => (trainer == null || x.Trainer.Id == trainer) && (club == null || x.Trainer.ClubId == club)).Select(x => new { x.Id, x.Date, x.Rule.Time, x.Rule.Name, x.Rule.Minutes, x.Rule.Description, x.Rule.Personal, x.Trainer, x.Rule.Capacity, remaining = x.Rule.Capacity - counts.GetValueOrDefault(x.Id), booked = mine.Contains(x.Id), started = x.Started });
        return Results.Ok(new { today = Catalog.Today, sessions });
    }

    static async Task<IResult> Reserve(string id, ClubDb db, Catalog c, HttpContext ctx)
    {
        var session = c.Find(id);
        string uid = ctx.MemberId();
        if (session == null || session.Started)
            return Accounts.Error("This session is no longer available.", 409);
        using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        if (await db.Bookings.AnyAsync(x => x.MemberId == uid && x.SlotId == id))
            return Results.NoContent();
        var datePrefix = session.Date.ToString("yyyy-MM-dd") + "_";
        var sameDay = await db.Bookings.Where(x => x.MemberId == uid && x.SlotId.StartsWith(datePrefix)).Select(x => x.SlotId).ToListAsync();
        var start = TimeOnly.Parse(session.Rule.Time);
        if (sameDay.Select(c.Find).OfType<Session>().Any(other => TimeOnly.Parse(other.Rule.Time) < start.AddMinutes(session.Rule.Minutes) && TimeOnly.Parse(other.Rule.Time).AddMinutes(other.Rule.Minutes) > start))
            return Accounts.Error("You already have a reservation at this time.", 409);
        var eligible = db.Enrollments.Where(x => x.MemberId == uid && x.Start <= session.Date && x.End > session.Date);
        var pass = session.Rule.Personal ? await eligible.Where(x => x.TrainingCredits > db.Bookings.Count(b => b.EnrollmentId == x.Id)).OrderBy(x => x.End).FirstOrDefaultAsync() : await eligible.Where(x => x.GroupAccess).FirstOrDefaultAsync();
        if (pass == null)
            return Accounts.Error(session.Rule.Personal ? "A coaching credit valid on this date is required." : "A Premium or Diamond membership covering this date is required.", 403);
        var slot = await db.Slots.FindAsync(id);
        if (slot == null)
        {
            slot = new ClassSlot
            {
                Id = id,
                Date = session.Date
            };
            db.Add(slot);
        }

        if (slot.Held >= session.Rule.Capacity)
            return Accounts.Error("This class is full.", 409);
        slot.Held++;
        db.Add(new Booking { MemberId = uid, SlotId = id, EnrollmentId = pass.Id });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return Results.NoContent();
    }

    static async Task<IResult> Cancel(string id, ClubDb db, Catalog c, HttpContext ctx)
    {
        using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var booking = await db.Bookings.SingleOrDefaultAsync(x => x.MemberId == ctx.MemberId() && x.SlotId == id);
        if (booking == null)
            return Results.NoContent();
        if (c.Find(id)is { Started: true })
            return Accounts.Error("This session has already started.", 409);
        var slot = await db.Slots.SingleAsync(x => x.Id == id);
        slot.Held--;
        db.Remove(booking);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return Results.NoContent();
    }
}
