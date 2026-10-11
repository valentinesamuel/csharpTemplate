using Acme.Service.Modules.Auth.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Acme.Service.Modules.Auth.Tests.Fixtures;

internal static class TestUsers
{
    public const string Password = "correct horse battery";

    /// <summary>Creates a user with <paramref name="email"/> through <see cref="UserManager{TUser}"/> and returns its id.</summary>
    public static async Task<Guid> CreateAsync(IServiceProvider services, string email, string? displayName = null)
    {
        await using var scope = services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AuthUser>>();
        var user = new AuthUser { UserName = email, Email = email, DisplayName = displayName, CreatedAt = DateTimeOffset.UtcNow };
        var result = await userManager.CreateAsync(user, Password);
        result.Succeeded.ShouldBeTrue(string.Join(", ", result.Errors.Select(e => e.Code)));
        return user.Id;
    }
}
