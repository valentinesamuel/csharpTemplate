namespace Acme.Service.Modules.Auth.Contracts;

/// <summary>In-process lookup of users for other modules (INV-01: the only way to read Auth data).</summary>
public interface IUserDirectory
{
    /// <summary>Returns the summary of the user <paramref name="userId"/>, or null when no such user exists.</summary>
    /// <param name="userId">The user id.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    Task<UserSummary?> GetUserSummaryAsync(Guid userId, CancellationToken cancellationToken);
}
