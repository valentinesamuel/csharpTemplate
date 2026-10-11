using Acme.Service.BuildingBlocks.Infra.Smtp;
using Acme.Service.BuildingBlocks.Notifications;
using Acme.Service.BuildingBlocks.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Acme.Service.Infra.Tests.Smtp;

public sealed class SmtpRegistrationTests
{
    private const string Password = "smtp-registration-secret-7d3a";

    [Fact]
    public void Registration_DeclaresNotificationsCategoryAndSmtpName()
    {
        var registration = new SmtpRegistration();

        registration.Category.ShouldBe(ProviderCategory.Notifications);
        registration.Name.ShouldBe("Smtp");
    }

    [Fact]
    public async Task Register_ValidConfiguration_ResolvesSingletonSenderWithoutConnecting()
    {
        await using var provider = Build(Valid());

        var sender = provider.GetRequiredService<INotificationSender>();

        sender.ShouldBeOfType<SmtpNotificationSender>();
        provider.GetRequiredService<INotificationSender>().ShouldBeSameAs(sender);
    }

    [Fact]
    public async Task Register_Defaults_Port587StartTlsAndThirtySecondTimeout()
    {
        await using var provider = Build(Valid());

        var options = provider.GetRequiredService<IOptions<SmtpOptions>>().Value;

        options.Port.ShouldBe(587);
        options.UseStartTls.ShouldBeTrue();
        options.Timeout.ShouldBe(TimeSpan.FromSeconds(30));
        options.Username.ShouldBeNull();
        options.Password.ShouldBeNull();
    }

    [Fact]
    public async Task Register_ConfiguredValues_AreBound()
    {
        await using var provider = Build(Valid(new()
        {
            ["Notifications:Smtp:Port"] = "2525",
            ["Notifications:Smtp:UseStartTls"] = "true",
            ["Notifications:Smtp:Username"] = "mailer",
            ["Notifications:Smtp:Password"] = Password,
        }));

        var options = provider.GetRequiredService<IOptions<SmtpOptions>>().Value;

        options.Host.ShouldBe("smtp.example.com");
        options.Port.ShouldBe(2525);
        options.Username.ShouldBe("mailer");
        options.Password.ShouldBe(Password);
        options.From.ShouldBe("Service <no-reply@example.com>");
    }

    [Theory]
    [InlineData("Notifications:Smtp:Host")]
    [InlineData("Notifications:Smtp:From")]
    public async Task Register_MissingRequiredSetting_ValidationNamesKey(string key)
    {
        await using var provider = Build(Valid(new() { [key] = null }));

        var exception = Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<SmtpOptions>>().Value);

        exception.Message.ShouldContain($"{key} is required when Notifications:Provider is Smtp.");
    }

    [Theory]
    [InlineData("Notifications:Smtp:Host", "not a host")]
    [InlineData("Notifications:Smtp:Port", "0")]
    [InlineData("Notifications:Smtp:Port", "65536")]
    [InlineData("Notifications:Smtp:Port", "smtp")]
    [InlineData("Notifications:Smtp:UseStartTls", "maybe")]
    [InlineData("Notifications:Smtp:From", "not-an-address")]
    [InlineData("Notifications:Smtp:From", "Service <>")]
    public async Task Register_InvalidSetting_ValidationNamesKey(string key, string value)
    {
        await using var provider = Build(Valid(new() { [key] = value }));

        var exception = Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<SmtpOptions>>().Value);

        exception.Message.ShouldContain(key);
    }

    [Fact]
    public async Task Register_PasswordWithoutUsername_ValidationNamesBothKeysWithoutEchoingPassword()
    {
        await using var provider = Build(Valid(new() { ["Notifications:Smtp:Password"] = Password }));

        var exception = Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<SmtpOptions>>().Value);

        exception.Message.ShouldContain("Notifications:Smtp:Username");
        exception.Message.ShouldContain("Notifications:Smtp:Password");
        exception.Message.ShouldNotContain(Password);
    }

    [Fact]
    public async Task Register_CredentialsWithoutStartTls_FailsValidationWithoutEchoingPassword()
    {
        await using var provider = Build(Valid(new()
        {
            ["Notifications:Smtp:UseStartTls"] = "false",
            ["Notifications:Smtp:Username"] = "mailer",
            ["Notifications:Smtp:Password"] = Password,
        }));

        var exception = Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<SmtpOptions>>().Value);

        exception.Message.ShouldContain("Notifications:Smtp:UseStartTls=true");
        exception.Message.ShouldNotContain(Password);
    }

    [Fact]
    public async Task Register_NoCredentialsWithoutStartTls_IsValid()
    {
        await using var provider = Build(Valid(new() { ["Notifications:Smtp:UseStartTls"] = "false" }));

        provider.GetRequiredService<IOptions<SmtpOptions>>().Value.UseStartTls.ShouldBeFalse();
    }

    private static Dictionary<string, string?> Valid(Dictionary<string, string?>? overrides = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Notifications:Smtp:Host"] = "smtp.example.com",
            ["Notifications:Smtp:From"] = "Service <no-reply@example.com>",
        };
        foreach (var (key, value) in overrides ?? [])
        {
            settings[key] = value;
        }

        return settings;
    }

    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        new SmtpRegistration().Register(services, configuration);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}
