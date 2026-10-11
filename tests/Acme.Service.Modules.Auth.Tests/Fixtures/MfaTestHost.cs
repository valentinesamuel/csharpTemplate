using System.Net;
using System.Net.Http.Json;
using Acme.Service.Testing.Containers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;

namespace Acme.Service.Modules.Auth.Tests.Fixtures;

/// <summary>
/// An <see cref="AccountWebFactory"/> over a migrated test database whose <see cref="TimeProvider"/> is a
/// <see cref="ManualTimeProvider"/>, so the tests know which authenticator time step the server sees.
/// </summary>
internal static class MfaTestHost
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<(AccountWebFactory Factory, ManualTimeProvider Clock)> CreateAsync(
        PostgresFixture postgres,
        IReadOnlyDictionary<string, string?>? overrides = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        await AuthTestServices.MigrateAsync(connectionString);

        // Mid-step, so a code computed now stays in the same step for the whole test unless the test advances the clock.
        var now = DateTimeOffset.UtcNow;
        var clock = new ManualTimeProvider(DateTimeOffset.FromUnixTimeSeconds((now.ToUnixTimeSeconds() / 30 * 30) + 15));
        var factory = new AccountWebFactory(connectionString, overrides, services =>
        {
            services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock));
            configureServices?.Invoke(services);
        });
        return (factory, clock);
    }

    /// <summary>
    /// Registers and confirms <paramref name="email"/>, enables two-factor authentication through the API and moves the
    /// clock to the next time step (the enabling code's step is used up). Returns the user, its shared key and the
    /// recovery codes.
    /// </summary>
    public static async Task<(Guid UserId, string SharedKey, IReadOnlyList<string> RecoveryCodes)> EnableMfaAsync(
        AccountClient client,
        ManualTimeProvider clock,
        string email)
    {
        var userId = await client.RegisterConfirmedAsync(email);
        var tokens = await client.LoginTokensAsync(email);

        using var setup = await client.MfaSetupAsync(tokens.AccessToken);
        setup.StatusCode.ShouldBe(HttpStatusCode.OK, await setup.Content.ReadAsStringAsync(Ct));
        var key = (await setup.Content.ReadFromJsonAsync<MfaSetupBody>(Ct))!.SharedKey;

        using var verify = await client.MfaVerifyAsync(tokens.AccessToken, Rfc6238.Code(key, clock.GetUtcNow()));
        verify.StatusCode.ShouldBe(HttpStatusCode.OK, await verify.Content.ReadAsStringAsync(Ct));
        var codes = (await verify.Content.ReadFromJsonAsync<RecoveryCodesBody>(Ct))!.RecoveryCodes;

        clock.Advance(Rfc6238.Step);
        return (userId, key, codes);
    }
}
