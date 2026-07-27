using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using MyStack.Auth.Data;
using Shouldly;

namespace MyStack.Auth.Tests;

// Identity over the real schema: if the naming convention, the schema or the Guid key were wrong,
// this is where it shows up rather than in the first account flow that tries to use them.
public sealed class IdentityStoreTests(AuthAppFixture app)
{
    private const string ValidPassword = "correct horse battery staple";

    [Fact]
    public async Task A_user_round_trips_through_the_store()
    {
        await using var scope = app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var email = UniqueEmail();
        var result = await users.CreateAsync(NewUser(email), ValidPassword);
        result.Succeeded.ShouldBeTrue(result.ToString());

        var stored = await users.FindByEmailAsync(email);
        stored.ShouldNotBeNull();
        stored.Id.ShouldNotBe(Guid.Empty);
        stored.EmailConfirmed.ShouldBeFalse();
    }

    [Fact]
    public async Task The_id_the_application_generated_is_the_one_that_is_stored()
    {
        await using var scope = app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var user = NewUser(UniqueEmail());
        var generated = user.Id;

        (await users.CreateAsync(user, ValidPassword)).Succeeded.ShouldBeTrue();

        // Nothing between here and the row substitutes a key of its own — the value the entity was
        // constructed with is what comes back.
        var stored = await users.FindByIdAsync(generated.ToString());
        stored.ShouldNotBeNull();
        stored.Id.ShouldBe(generated);

        // Version 7 puts a millisecond timestamp in the leading bits, and Postgres orders `uuid` by
        // that same byte order — which is the whole reason for the key choice.
        generated.Version.ShouldBe(7);
    }

    [Fact]
    public async Task An_email_cannot_be_registered_twice()
    {
        await using var scope = app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var email = UniqueEmail();
        (await users.CreateAsync(NewUser(email), ValidPassword)).Succeeded.ShouldBeTrue();

        var duplicate = await users.CreateAsync(NewUser(email), ValidPassword);

        duplicate.Succeeded.ShouldBeFalse();
        duplicate.Errors.Select(error => error.Code).ShouldContain("DuplicateEmail");
    }

    [Fact]
    public async Task A_password_under_the_minimum_length_is_refused()
    {
        await using var scope = app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var result = await users.CreateAsync(NewUser(UniqueEmail()), "Sh0rt!pass");

        result.Succeeded.ShouldBeFalse();
        result.Errors.Select(error => error.Code).ShouldContain("PasswordTooShort");
    }

    [Fact]
    public async Task A_long_password_without_digits_or_symbols_is_accepted()
    {
        await using var scope = app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var result = await users.CreateAsync(
            NewUser(UniqueEmail()),
            "a passphrase of ordinary words"
        );

        result.Succeeded.ShouldBeTrue(result.ToString());
    }

    private static string UniqueEmail() => $"user-{Guid.CreateVersion7():n}@mystack.test";

    private static ApplicationUser NewUser(string email) =>
        new() { UserName = email, Email = email };
}
