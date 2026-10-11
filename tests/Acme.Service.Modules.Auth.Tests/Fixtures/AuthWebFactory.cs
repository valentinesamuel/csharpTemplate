using Acme.Service.Testing;

namespace Acme.Service.Modules.Auth.Tests.Fixtures;

/// <summary>The real web host over one test database.</summary>
internal sealed class AuthWebFactory(string connectionString) : ServiceWebFactory
{
    protected override IReadOnlyDictionary<string, string?> ConfigurationOverrides => new Dictionary<string, string?>
    {
        ["ConnectionStrings:Postgres"] = connectionString,
    };
}
