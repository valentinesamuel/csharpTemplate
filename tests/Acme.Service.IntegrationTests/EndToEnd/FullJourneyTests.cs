// <sample:orders>
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Acme.Service.BuildingBlocks.Infra.Hangfire;
using Acme.Service.IntegrationTests.Fixtures;
using Acme.Service.Modules.Auth;
using Acme.Service.Testing.Containers;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Shouldly;

namespace Acme.Service.IntegrationTests.EndToEnd;

/// <summary>
/// The settled plan's verification journey on the production providers: Postgres, Redis cache, S3 blobs (RustFS),
/// SMTP (Mailpit) and Hangfire jobs, with the schema created by the real <c>migrate</c> verb first. It exercises the
/// Hangfire job runner (verification and order emails) and the S3 302 download path end to end.
/// </summary>
public sealed class FullJourneyTests(PostgresFixture postgres, RedisFixture redis, RustFsFixture rustFs, MailpitFixture mailpit)
    : IClassFixture<PostgresFixture>, IClassFixture<RedisFixture>, IClassFixture<RustFsFixture>, IClassFixture<MailpitFixture>
{
    private static readonly TimeSpan _emailTimeout = TimeSpan.FromSeconds(60);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task FullJourney_ProductionProviders_RegisterToAttachmentRoundTrip()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        await ServiceApi.MigrateAsync(connectionString, jobsProvider: "Hangfire");
        await using var factory = new IntegrationWebFactory(connectionString, ProductionProviders(), services =>
            services.Configure<HangfireOptions>(o =>
            {
                // Hangfire polls every 15 s by default; poll fast so the emails arrive quickly.
                o.QueuePollInterval = TimeSpan.FromMilliseconds(200);
                o.SchedulePollingInterval = TimeSpan.FromSeconds(1);
            }));
        var api = new ServiceApi(factory);
        var email = $"journey-{Guid.NewGuid():N}@example.com";

        // 1-3. Register, read the verification email through Mailpit's API (outbox -> Hangfire job -> SMTP), confirm.
        await api.RegisterAsync(email);
        var verification = await mailpit.FindBySubjectAsync("Confirm your email address", _emailTimeout, Ct);
        verification.To.ShouldHaveSingleItem().Address.ShouldBe(email);
        var link = new Uri(verification.Text.Split('\n').Select(l => l.Trim()).Single(l => l.StartsWith("http", StringComparison.Ordinal)));
        var query = QueryHelpers.ParseQuery(link.Query);
        using (var confirm = await api.PostAsync("/api/v1/auth/confirm-email", new { userId = query["userId"].ToString(), token = query["token"].ToString() }))
        {
            confirm.StatusCode.ShouldBe(HttpStatusCode.NoContent, await confirm.Content.ReadAsStringAsync(Ct));
        }

        // 4. Login.
        var password = await api.LoginTokensAsync(email);
        Amr(password.AccessToken).ShouldBe(["pwd"]);

        // 5-6. Enable MFA, then the login needs the second step.
        var sharedKey = await api.EnableMfaAsync(password.AccessToken);
        var mfaToken = await api.LoginMfaTokenAsync(email);
        using var mfaLogin = await api.CompleteMfaAsync(mfaToken, sharedKey);
        mfaLogin.StatusCode.ShouldBe(HttpStatusCode.OK, await mfaLogin.Content.ReadAsStringAsync(Ct));
        var signedIn = await ServiceApi.TokensOfAsync(mfaLogin);
        Amr(signedIn.AccessToken).ShouldBe(["pwd", "mfa"]);

        // 7. Refresh rotates the pair.
        using var refresh = await api.RefreshAsync(signedIn.RefreshToken);
        refresh.StatusCode.ShouldBe(HttpStatusCode.OK);
        var customer = await ServiceApi.TokensOfAsync(refresh);
        customer.RefreshToken.ShouldNotBe(signedIn.RefreshToken);

        // 8. Orders: create (idempotent through Redis), list, get.
        var order = new { currency = "EUR", lines = new[] { new { sku = "SKU-1", quantity = 2, unitPrice = 12.5m } } };
        var key = ("Idempotency-Key", Guid.NewGuid().ToString("N"));
        using var created = await api.PostAsync("/api/v1/orders", order, customer.AccessToken, key);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var orderId = (await ServiceApi.JsonAsync(created)).GetProperty("id").GetGuid();
        using (var replayed = await api.PostAsync("/api/v1/orders", order, customer.AccessToken, key))
        {
            replayed.StatusCode.ShouldBe(HttpStatusCode.Created);
            (await ServiceApi.JsonAsync(replayed)).GetProperty("id").GetGuid().ShouldBe(orderId);
        }

        using (var list = await api.GetAsync("/api/v1/orders", customer.AccessToken))
        {
            list.StatusCode.ShouldBe(HttpStatusCode.OK);
            var page = await ServiceApi.JsonAsync(list);
            page.GetProperty("totalCount").GetInt32().ShouldBe(1);
            page.GetProperty("items")[0].GetProperty("id").GetGuid().ShouldBe(orderId);
        }

        using (var get = await api.GetAsync($"/api/v1/orders/{orderId}", customer.AccessToken))
        {
            get.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await ServiceApi.JsonAsync(get);
            body.GetProperty("status").GetString().ShouldBe("Pending");
            body.GetProperty("total").GetDecimal().ShouldBe(25.00m);
        }

        // The OrderPlaced event reached the customer as an email (outbox -> handler -> Hangfire job -> SMTP).
        var placed = await mailpit.FindBySubjectAsync(
            string.Create(CultureInfo.InvariantCulture, $"Your order {orderId} was placed"), _emailTimeout, Ct);
        placed.To.ShouldHaveSingleItem().Address.ShouldBe(email);
        await ServiceApi.EventuallyAsync(
            async () => await api.ScalarAsync<long>("SELECT count(*) FROM hangfire.job WHERE statename = 'Succeeded'") >= 2,
            "Hangfire records both email jobs as succeeded");

        // 9. Authorization: no token 401; a Customer cannot confirm (403); granted Staff, after a refresh, can (200).
        using (var anonymous = await api.GetAsync("/api/v1/orders"))
        {
            anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        var confirmPath = $"/api/v1/orders/{orderId}/confirm";
        using (var forbidden = await api.PostAsync(confirmPath, null, customer.AccessToken))
        {
            forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        (await AuthModule.GrantRoleAsync(factory.Services, email, "Staff", Ct)).ShouldBe(0);
        using var staffRefresh = await api.RefreshAsync(customer.RefreshToken);
        staffRefresh.StatusCode.ShouldBe(HttpStatusCode.OK);
        var staff = await ServiceApi.TokensOfAsync(staffRefresh);
        using (var confirmed = await api.PostAsync(confirmPath, null, staff.AccessToken))
        {
            confirmed.StatusCode.ShouldBe(HttpStatusCode.OK, await confirmed.Content.ReadAsStringAsync(Ct));
            (await ServiceApi.JsonAsync(confirmed)).GetProperty("status").GetString().ShouldBe("Confirmed");
        }

        // 10. Attachment round trip on S3: upload, then download through a 302 to a presigned URL.
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n" + new string('j', 4096));
        using var file = new ByteArrayContent(pdf);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        using var form = new MultipartFormDataContent { { file, "file", "journey.pdf" } };
        using var uploaded = await api.SendAsync(HttpMethod.Post, $"/api/v1/orders/{orderId}/attachments", staff.AccessToken, form);
        uploaded.StatusCode.ShouldBe(HttpStatusCode.Created, await uploaded.Content.ReadAsStringAsync(Ct));

        using var download = await api.GetAsync(uploaded.Headers.Location!.ToString(), staff.AccessToken);
        download.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var presigned = download.Headers.Location!;
        presigned.ToString().ShouldStartWith(rustFs.ServiceUrl);
        using var http = new HttpClient();
        using var fetched = await http.GetAsync(presigned, Ct);
        fetched.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await fetched.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(pdf);
    }

    private static string[] Amr(string accessToken) =>
        [.. new JsonWebToken(accessToken).Claims.Where(c => c.Type == "amr").Select(c => c.Value)];

    private Dictionary<string, string?> ProductionProviders() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Cache:Provider"] = "Redis",
        ["Cache:Redis:ConnectionString"] = redis.ConnectionString,
        ["Blob:Provider"] = "S3",
        ["Blob:S3:ServiceUrl"] = rustFs.ServiceUrl,
        ["Blob:S3:Region"] = RustFsFixture.Region,
        ["Blob:S3:Bucket"] = "journey-" + Guid.NewGuid().ToString("N")[..12],
        ["Blob:S3:AccessKey"] = RustFsFixture.AccessKey,
        ["Blob:S3:SecretKey"] = RustFsFixture.SecretKey,
        ["Blob:S3:ForcePathStyle"] = "true",
        ["Blob:S3:CreateIfMissing"] = "true",
        ["Notifications:Provider"] = "Smtp",
        ["Notifications:Smtp:Host"] = mailpit.SmtpHost,
        ["Notifications:Smtp:Port"] = mailpit.SmtpHostPort.ToString(CultureInfo.InvariantCulture),
        ["Notifications:Smtp:UseStartTls"] = "false",
        ["Notifications:Smtp:From"] = "no-reply@example.test",
        ["Jobs:Provider"] = "Hangfire",
        ["Jobs:Hangfire:WorkerCount"] = "2",
        ["Locks:Provider"] = "Postgres",
    };
}

// </sample:orders>
