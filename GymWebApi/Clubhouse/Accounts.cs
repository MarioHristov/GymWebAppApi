using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GymWebApi.Clubhouse;
public record Credentials(string? Email, string? Password);
public record AccountChange(string? CurrentPassword, string? Value);
public record Details(string? Name, DateOnly? BirthDate, bool Student);
public static class Accounts
{
    public static string MemberId(this HttpContext ctx) => ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    public static IResult Error(string text, int status = 400) => Results.Json(new { error = text }, statusCode: status);
    static bool EmailOk(string? email) => email is { Length: > 2 and <= 254 } && new EmailAddressAttribute().IsValid(email);
    static bool PasswordOk(string? value) => value is { Length: >= 8 and <= 128 };
    static async Task<AuthenticationTicket> SignIn(HttpContext ctx, IdentityUser user)
    {
        var claims = new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, user.Id), new Claim("stamp", user.SecurityStamp ?? "") }, "member");
        var principal = new ClaimsPrincipal(claims);
        var props = new AuthenticationProperties
        {
            IssuedUtc = DateTimeOffset.UtcNow,
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
        };
        await ctx.SignInAsync("member", principal, props);
        return new AuthenticationTicket(principal, props, "member");
    }

    public static void Map(WebApplication app)
    {
        app.MapPost("/api/account/register", Register).RequireRateLimiting("accounts");
        app.MapPost("/UserProfile/CreateUser", Register).RequireRateLimiting("accounts");
        app.MapPost("/api/account/login", Login).RequireRateLimiting("accounts");
        app.MapPost("/UserProfile/LogUser", Login).RequireRateLimiting("accounts");
        app.MapPost("/api/account/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync("member");
            return Results.NoContent();
        });
        app.MapPut("/api/account/{field}", Change).RequireAuthorization().RequireRateLimiting("accounts");
        app.MapPut("/api/profile", async (Details input, HttpContext ctx, ClubDb db) =>
        {
            if (input.Name?.Trim()is not { Length: > 0 and <= 100 } name || input.BirthDate > Catalog.Today || input.BirthDate < Catalog.Today.AddYears(-120))
                return Error("Enter a name and a valid date of birth.");
            var member = await db.Members.FindAsync(ctx.MemberId());
            if (member == null)
            {
                member = new Member
                {
                    Id = ctx.MemberId()
                };
                db.Add(member);
            }

            member.Name = name;
            member.BirthDate = input.BirthDate;
            member.Student = input.Student;
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireAuthorization();
    }

    static async Task<IResult> Register(Credentials input, AccountDb db, IPasswordHasher<IdentityUser> hasher, HttpContext ctx)
    {
        string? email = input.Email?.Trim();
        if (!EmailOk(email) || !PasswordOk(input.Password))
            return Error("Enter a valid email and a password of 8–128 characters.");
        string normalized = email!.ToUpperInvariant();
        if (await db.Users.AnyAsync(u => u.NormalizedUserName == normalized || u.NormalizedEmail == normalized))
            return Error("Email already in use.", 409);
        var user = new IdentityUser(email)
        {
            Email = email,
            NormalizedEmail = normalized,
            NormalizedUserName = normalized,
            SecurityStamp = Guid.NewGuid().ToString(),
            LockoutEnabled = true
        };
        user.PasswordHash = hasher.HashPassword(user, input.Password!);
        db.Add(user);
        await db.SaveChangesAsync();
        await SignIn(ctx, user);
        return Results.Ok("User was successfully created");
    }

    static async Task<IResult> Login(Credentials input, AccountDb db, IPasswordHasher<IdentityUser> hasher, HttpContext ctx, IOptionsMonitor<CookieAuthenticationOptions> options)
    {
        string normalized = (input.Email ?? "").Trim().ToUpperInvariant();
        var user = await db.Users.FirstOrDefaultAsync(x => x.NormalizedUserName == normalized || x.NormalizedEmail == normalized);
        if (user?.PasswordHash == null || input.Password is not { Length: > 0 and <= 128 } || user.TwoFactorEnabled || user.LockoutEnd > DateTimeOffset.UtcNow || hasher.VerifyHashedPassword(user, user.PasswordHash, input.Password) == PasswordVerificationResult.Failed)
            return Error("Unable to sign in with those credentials.", 401);
        if (user.SecurityStamp == null)
        {
            user.SecurityStamp = Guid.NewGuid().ToString();
            await db.SaveChangesAsync();
        }

        var ticket = await SignIn(ctx, user);
        if (ctx.Request.Path.StartsWithSegments("/UserProfile"))
            return Results.Ok(new { message = "User was successfully loggedin", token = options.Get("member").TicketDataFormat.Protect(ticket) });
        return Results.Ok(new { user.Email });
    }

    static async Task<IResult> Change(string field, AccountChange input, AccountDb db, IPasswordHasher<IdentityUser> hasher, HttpContext ctx)
    {
        var user = await db.Users.FindAsync(ctx.MemberId());
        if (user?.PasswordHash == null || input.CurrentPassword is not { Length: > 0 and <= 128 } || hasher.VerifyHashedPassword(user, user.PasswordHash, input.CurrentPassword) == PasswordVerificationResult.Failed)
            return Error("Current password is incorrect.", 403);
        string? value = input.Value;
        if (field == "email" && EmailOk(value?.Trim()))
        {
            value = value!.Trim();
            string normalized = value.ToUpperInvariant();
            if (await db.Users.AnyAsync(x => x.Id != user.Id && (x.NormalizedUserName == normalized || x.NormalizedEmail == normalized)))
                return Error("Email already in use.", 409);
            user.UserName = user.Email = value;
            user.NormalizedUserName = user.NormalizedEmail = normalized;
            user.EmailConfirmed = false;
        }
        else if (field == "password" && PasswordOk(value))
            user.PasswordHash = hasher.HashPassword(user, value!);
        else
            return Error("Enter a valid email or a password of 8–128 characters.");
        user.SecurityStamp = Guid.NewGuid().ToString();
        user.ConcurrencyStamp = Guid.NewGuid().ToString();
        await db.SaveChangesAsync();
        await SignIn(ctx, user);
        return Results.NoContent();
    }
}
