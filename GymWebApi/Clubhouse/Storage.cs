using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using System.ComponentModel.DataAnnotations;

namespace GymWebApi.Clubhouse;
// Keep the Identity table and password format compatible with the original SQL Server database.
public sealed class AccountDb(DbContextOptions<AccountDb> options) : DbContext(options)
{
    public DbSet<IdentityUser> Users => Set<IdentityUser>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        var user = model.Entity<IdentityUser>();
        user.ToTable("AspNetUsers");
        user.Property(x => x.Id).HasMaxLength(450);
        user.Property(x => x.NormalizedUserName).HasMaxLength(256);
        user.Property(x => x.NormalizedEmail).HasMaxLength(256);
        user.Property(x => x.ConcurrencyStamp).IsConcurrencyToken();
        user.HasIndex(x => x.NormalizedUserName).IsUnique();
    }
}

public class Member
{
    [Key, MaxLength(450)]
    public string Id { get; set; } = "";

    [MaxLength(100)]
    public string Name { get; set; } = "";
    public DateOnly? BirthDate { get; set; }
    public bool Student { get; set; }
}

public class Enrollment
{
    public int Id { get; set; }

    [MaxLength(450)]
    public string MemberId { get; set; } = "";

    [MaxLength(80)]
    public string Plan { get; set; } = "";
    public DateOnly Start { get; set; }
    public DateOnly End { get; set; } // Exclusive. Training packs are recorded separately from club access.
    public int Months { get; set; }
    public int TotalCents { get; set; }
    public bool GroupAccess { get; set; }
    public int TrainingCredits { get; set; }
    public int DiscountPercent { get; set; }
    public Guid RequestId { get; set; }
}

public class ClassSlot
{
    [Key, MaxLength(100)]
    public string Id { get; set; } = "";
    public DateOnly Date { get; set; }
    public int Held { get; set; }
}

public class Booking
{
    public int Id { get; set; }

    [MaxLength(450)]
    public string MemberId { get; set; } = "";

    [MaxLength(100)]
    public string SlotId { get; set; } = "";
    public int EnrollmentId { get; set; }
}

public sealed class ClubDb(DbContextOptions<ClubDb> options) : DbContext(options)
{
    public DbSet<Member> Members => Set<Member>();
    public DbSet<Enrollment> Enrollments => Set<Enrollment>();
    public DbSet<ClassSlot> Slots => Set<ClassSlot>();
    public DbSet<Booking> Bookings => Set<Booking>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Member>().ToTable("GymMemberProfiles");
        model.Entity<Enrollment>().ToTable("GymEnrollments").HasIndex(x => new { x.MemberId, x.RequestId }).IsUnique();
        model.Entity<ClassSlot>().ToTable("GymClassSlots").Property(x => x.Held).IsConcurrencyToken();
        model.Entity<Booking>().ToTable("GymBookings").HasIndex(x => new { x.MemberId, x.SlotId }).IsUnique();
        model.Entity<Booking>().HasOne<ClassSlot>().WithMany().HasForeignKey(x => x.SlotId);
        model.Entity<Booking>().HasOne<Enrollment>().WithMany().HasForeignKey(x => x.EnrollmentId);
    }
}

public static class Storage
{
    // The account database can already contain Identity tables. Add only the new table group.
    public static async Task Initialize(DbContext db, string sentinel)
    {
        var creator = db.GetService<IRelationalDatabaseCreator>();
        if (!await creator.ExistsAsync())
            await creator.CreateAsync();
        await db.Database.OpenConnectionAsync();
        try
        {
            using var cmd = db.Database.GetDbConnection().CreateCommand();
            cmd.CommandText = db.Database.IsSqlite() ? "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@name" : "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA='dbo' AND TABLE_NAME=@name";
            var p = cmd.CreateParameter();
            p.ParameterName = "@name";
            p.Value = sentinel;
            cmd.Parameters.Add(p);
            if (Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 0)
                await creator.CreateTablesAsync();
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}
