using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tankstat.Application.Auth;
using Tankstat.TestSupport;

namespace Tankstat.Api.IntegrationTests;

/// <summary>
/// What an operator finds in the log of the real app: the errors nobody told the client about, refusals, sign-ins, and the start. Run
/// against the whole host (real GraphQL, real database), because the part that matters here is how HotChocolate hands its errors over.
/// Lines are found by their class and level and read through the values of their placeholders, not through their wording.
/// </summary>
[Collection(ApiCollection.Name)]
public class LoggingTests
{
    private const string Listener = "Tankstat.Api.GraphQL.GraphQLLoggingListener";
    private const string AuthService = "Tankstat.Application.Users.AuthService";
    private const string SetupService = "Tankstat.Application.Setup.SetupService";

    /// <summary>What the app itself logged (the framework's lines are not what is tested here).</summary>
    private static IEnumerable<LogEntry> Ours(TestApp app) => app.Log.From("Tankstat");

    /// <summary>
    /// A sender with a bug of its own: not an <see cref="EmailSendException"/> (a mail server that is down is one, and gets the administrator
    /// the link to hand over, see <c>StandaloneAuthTests</c>), so it is the server's unexpected error.
    /// </summary>
    private sealed class BrokenEmail : IEmailSender
    {
        public bool IsConfigured => true;
        public Task SendAsync(string to, string subject, string body, CancellationToken ct) => throw new InvalidOperationException("the sender is broken");
    }

    [Fact]
    public async Task ACancelledField_AndAnErrorWithoutAnException_AreDebugLines_NamingTheFieldAndTheCode()
    {
        // Two fields only this test adds to the schema: what a resolver does when the request is aborted, and an error it reports by code.
        using var app = new TestApp(new() { ["Auth:Mode"] = "None" }, s => s.AddGraphQLServer().AddTypeExtension<FaultyQueries>());
        var client = app.NewClient();

        var cancelled = await client.PostAsJsonAsync("/graphql", new { query = "{ cancelled }" });
        var refused = await client.PostAsJsonAsync("/graphql", new { query = "{ refused }" });

        Assert.Contains("errors", await cancelled.Content.ReadAsStringAsync());
        Assert.Contains("TEST_REFUSED", await refused.Content.ReadAsStringAsync());
        Assert.DoesNotContain(Ours(app), e => e.Level >= LogLevel.Warning); // neither is an error of the server's
        var stopped = Assert.Single(app.Log.From(Listener), e => e.Values.GetValueOrDefault("Field") as string == "Query.cancelled");
        var coded = Assert.Single(app.Log.From(Listener), e => e.Values.GetValueOrDefault("Field") as string == "Query.refused");
        Assert.Equal((LogLevel.Debug, LogLevel.Debug), (stopped.Level, coded.Level));
        Assert.Equal("TEST_REFUSED", coded.Values["Code"]);
        Assert.False(app.Log.Mentions("the words of the error")); // its code only, never its message
    }

    [Fact]
    public async Task ANullInANonNullField_IsAWarning_NamingTheSchemaField_NeverTheClientsAliasOrTheMessage()
    {
        // A resolver that returns null for a field that must not be null: always the server's bug, and no hook of HotChocolate's tells.
        using var app = new TestApp(new() { ["Auth:Mode"] = "None" }, s => s.AddGraphQLServer().AddTypeExtension<NullQueries>());
        var client = app.NewClient();

        var root = await client.Gql("query Root { nullRoot }");
        var nested = await client.Gql("query Nested { x: nullHolder { y: nullNested } }");

        Assert.Equal(("HC0018", "HC0018"), (root.ErrorCode(), nested.ErrorCode()));
        var warnings = app.Log.From(Listener).Where(e => e.Level == LogLevel.Warning).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.Equal(["Query.nullRoot", "NullHolder.nullNested"], warnings.Select(e => e.Values["Field"])); // by the schema, never the aliases x and y
        Assert.All(warnings, e => Assert.Equal(("HC0018", "anonymous"), (e.Values["Code"], e.Values["UserId"])));
        Assert.Equal(["query Root", "query Nested"], warnings.Select(e => e.Values["Operation"]));
        Assert.DoesNotContain(Ours(app), e => e.Level >= LogLevel.Error); // no exception to attach
        Assert.False(app.Log.Mentions("Cannot return null")); // never the message
    }

    [Fact]
    public void AUserIsNamedInTheLogByTheirIdOnly_AndAnythingElseIsAnonymous()
    {
        var id = Guid.NewGuid();
        System.Security.Claims.ClaimsPrincipal With(string value) =>
            new(new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, value)], "test"));

        Assert.Equal(id.ToString(), Tankstat.Api.Auth.RequestUser.Id(With(id.ToString("N")))); // one spelling of an id
        Assert.Equal("anonymous", Tankstat.Api.Auth.RequestUser.Id(With("alice@example.com\nwarn: forged")));
        Assert.Equal("anonymous", Tankstat.Api.Auth.RequestUser.Id(null));
    }

    [Fact]
    public async Task ABusinessError_IsOnlyADebugLine_NamingTheKey()
    {
        using var app = new TestApp(new() { ["Auth:Mode"] = "None" });

        var body = await app.NewClient().Gql("mutation { addVehicle(input: { name: \" \", fuelType: PETROL }) { id } }");

        Assert.Equal("VALIDATION_FAILED", body.ErrorCode());
        Assert.DoesNotContain(Ours(app), e => e.Level >= LogLevel.Warning);
        var line = Assert.Single(app.Log.From(Listener), e => e.Values.ContainsKey("Key"));
        Assert.Equal(LogLevel.Debug, line.Level);
        Assert.Equal("vehicle.nameRequired", line.Values["Key"]);
    }

    [Fact]
    public async Task EveryRequestIsTimed_ByTheOperationItRan()
    {
        using var app = new TestApp(new() { ["Auth:Mode"] = "None" });
        var client = app.NewClient();

        await client.Gql("mutation { addVehicle(input: { name: \"Car\", fuelType: PETROL }) { id } }");
        await client.Gql("query Mine { myVehicles { id } }");

        var timed = app.Log.From(Listener).Where(e => e.Values.ContainsKey("Ms")).ToList();
        Assert.All(timed, e => Assert.Equal(LogLevel.Debug, e.Level));
        Assert.Equal<object?[]>(["mutation (unnamed)", "query Mine"], timed.Select(e => e.Values["Operation"]).ToArray());
    }

    [Fact]
    public async Task ARefusal_IsAWarning_NamingTheKey()
    {
        using var app = new TestApp(new() { ["Auth:Mode"] = "None" });

        var body = await app.NewClient().Gql("{ users { id } }");

        Assert.Equal("FORBIDDEN", body.ErrorCode());
        var warning = Assert.Single(Ours(app), e => e.Level == LogLevel.Warning);
        Assert.Equal(Listener, warning.Category);
        Assert.Equal("auth.adminRequired", warning.Values["Key"]);
        Assert.Equal("Query.users", warning.Values["Field"]);
    }

    [Fact]
    public async Task AFieldIsNamedAsTheSchemaNamesIt_NotByTheAliasTheClientGaveIt()
    {
        using var app = new TestApp(new() { ["Auth:Mode"] = "None" });
        var alias = new string('z', 5000); // a letter no id shape holds (CapturedLog.Mentions skips a find inside one)

        var response = await app.NewClient().PostAsJsonAsync("/graphql", new { query = $"{{ {alias}: users {{ id }} }}" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var warning = Assert.Single(Ours(app), e => e.Level == LogLevel.Warning);
        Assert.Equal("Query.users", warning.Values["Field"]);
        Assert.False(app.Log.Mentions("zzzz")); // one request must not be able to put 5,000 characters of its own text in the log
    }

    [Fact]
    public async Task AnUnexpectedException_IsLoggedOnce_WithItsException_WhileTheClientOnlySeesTheGenericMessage()
    {
        using var app = new TestApp(new()
        {
            ["Auth:Mode"] = "Standalone",
            ["Auth:PublicUrl"] = "https://tank.test",
        }, s => s.AddSingleton<IEmailSender>(new BrokenEmail())); // the last registration wins: sending the setup link fails unexpectedly
        var admin = app.NewClient();
        await admin.LoginAs("root@example.com", "initial-password-1");

        var body = await admin.Gql("mutation($i: CreateUserInput!) { createUser(input: $i) { user { id } } }",
            new { i = new { email = "new.person@example.com", displayName = "New Person", isAdmin = false } });

        Assert.Equal("Unexpected Execution Error", body.GetProperty("errors")[0].GetProperty("message").GetString());
        var error = Assert.Single(app.Log.Entries, e => e.Level == LogLevel.Error); // once, although several hooks may hear of it
        Assert.Equal(Listener, error.Category);
        Assert.Equal("the sender is broken", error.Exception!.Message);
        Assert.Equal("Mutation.createUser", error.Values["Field"]);
        // what was done before it failed is on record
        Assert.Contains(app.Log.From("Tankstat.Application.Users.UserService"), e => e.Level == LogLevel.Information && e.Values.ContainsKey("AdminId"));
        Assert.False(app.Log.Mentions("new.person@example.com"));
    }

    [Fact]
    public async Task SigningIn_AndOut_IsLoggedByUserId_NeverByAddressOrPassword()
    {
        using var app = TestApp.Standalone();
        var client = app.NewClient(); // starts the host, which says what it did at the start (another test)
        app.Log.Clear();

        var wrong = await client.Gql("mutation($i: LoginInput!) { login(input: $i) { id } }", new { i = new { email = "root@example.com", password = "not-the-password-1" } });
        var admin = await client.LoginAs("root@example.com", "initial-password-1");
        var id = admin.GetProperty("id").GetString()!;
        await client.Gql("mutation { logout }");

        Assert.Equal("INVALID_CREDENTIALS", wrong.ErrorCode());
        var byId = (LogEntry e) => e.Values["UserId"]?.ToString() == id;
        Assert.Single(app.Log.From(AuthService), e => e.Level == LogLevel.Warning && byId(e)); // the wrong password
        Assert.Single(app.Log.From(AuthService), e => e.Level == LogLevel.Information && byId(e)); // the sign-in
        Assert.Single(app.Log.From("Tankstat.Api.GraphQL.AuthMutations"), e => e.Level == LogLevel.Information && byId(e)); // the sign-out
        foreach (var secret in new[] { "root@example.com", "initial-password-1", "not-the-password-1" })
            Assert.False(app.Log.Mentions(secret), secret);
    }

    [Fact]
    public async Task ARestRefusal_IsAWarning_NamingTheRouteAndTheUser_WithTheUserInTheScope()
    {
        using var app = TestApp.Standalone();
        var admin = app.NewClient();
        await admin.LoginAs("root@example.com", "initial-password-1");
        async Task<(HttpClient Client, string Id)> Create(string name)
        {
            var created = (await admin.Gql("mutation($i: CreateUserInput!) { createUser(input: $i) { user { id } reset { token } } }", new { i = new { email = $"{name}@example.com", displayName = name, isAdmin = false } })).Data().GetProperty("createUser");
            await app.NewClient().Gql("mutation($i: ResetPasswordInput!) { resetPassword(input: $i) }", new { i = new { token = created.GetProperty("reset").GetProperty("token").GetString(), newPassword = name + "-password-1", newPasswordConfirmation = name + "-password-1" } });
            var client = app.NewClient();
            await client.LoginAs($"{name}@example.com", name + "-password-1");
            return (client, created.GetProperty("user").GetProperty("id").GetString()!);
        }
        var (alice, aliceId) = await Create("alice");
        var (bob, bobId) = await Create("bob");
        var car = (await alice.Gql("mutation { addVehicle(input: { name: \"Car\", fuelType: PETROL }) { id } }")).Data().GetProperty("addVehicle").GetProperty("id").GetString()!;
        await admin.Gql("mutation($i: SetAccessGrantInput!) { setAccessGrant(input: $i) }", new { i = new { ownerId = aliceId, granteeId = bobId, level = "VIEW" } });
        var picture = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4]);
        picture.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        app.Log.Clear();

        var refused = await bob.PutAsync($"/media/vehicles/{car}/picture", picture); // a viewer may not change the vehicle
        var missing = await bob.GetAsync("/media/" + Guid.NewGuid());                // and a picture that is not there is no business of the log

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var warning = Assert.Single(Ours(app), e => e.Level >= LogLevel.Warning);
        Assert.Equal<object?[]>(["PUT", "/media/vehicles/{vehicleId:guid}/picture", "vehicle.viewOnly", bobId],
            [warning.Values["Method"], warning.Values["Route"], warning.Values["Key"], warning.Values["UserId"]?.ToString()]);
        Assert.DoesNotContain(car, warning.Message); // the route pattern, never the path that was asked for
        Assert.Contains($"UserId={bobId}", warning.ScopeText); // the scope of the request names the user for every line written meanwhile
    }

    [Fact]
    public async Task AStart_LogsTheMigrationsAndTheFirstAdministrator()
    {
        // A new, empty database (not the migrated copy the other hosts start from), so this start applies the migrations itself.
        // The first administrator is the setup page's to make (#149), with the code the start logs.
        using var app = new TestApp(new Dictionary<string, string?> { ["Auth:Mode"] = "Standalone", ["Setup:Enabled"] = "true" }, migrated: false);

        var client = app.NewClient(); // starts the host
        var code = (string)Assert.Single(app.Log.From<Tankstat.Application.Setup.OneTimeCode.SetupLine>(), e => e.Values.ContainsKey("Code")).Values["Code"]!;
        (await client.Gql("mutation($i: SetupFinishInput!) { setupFinish(input: $i) { restarting } }",
            new { i = new { code, email = "root@example.com", password = "initial-password-1", passwordConfirmation = "initial-password-1" } })).Data();

        Assert.Contains(app.Log.From("Tankstat.Infrastructure.Persistence.DatabaseMigrator"), e => e.Level == LogLevel.Information && e.Values.ContainsKey("Migrations"));
        Assert.Contains(app.Log.From(SetupService), e => e.Level == LogLevel.Information && e.Values.ContainsKey("UserId"));
        Assert.False(app.Log.Mentions("root@example.com"));
        Assert.False(app.Log.Mentions("initial-password-1"));
    }

    [Fact]
    public async Task AQueryThatDoesNotValidate_IsADebugLine_WithItsCodes_AndNotWhatTheClientWrote()
    {
        using var app = new TestApp(new() { ["Auth:Mode"] = "None" });

        var response = await app.NewClient().PostAsJsonAsync("/graphql", new { query = "{ nothingLikeThis }" });

        Assert.Contains("errors", await response.Content.ReadAsStringAsync()); // the client is told what is wrong with its query...
        Assert.DoesNotContain(Ours(app), e => e.Level >= LogLevel.Warning);
        var line = Assert.Single(app.Log.From(Listener), e => e.Values.ContainsKey("Codes"));
        Assert.Equal(LogLevel.Debug, line.Level);
        Assert.False(app.Log.Mentions("nothingLikeThis")); // ...the log only that it did not validate, and the error codes
    }

    [Fact]
    public async Task ARequestTheEngineTurnsDown_ForAMissingVariable_IsADebugLine_NotAnErrorWithItsText()
    {
        using var app = new TestApp(new() { ["Auth:Mode"] = "None" });

        // The mutation wants $i and the request sends none: the engine refuses it before anything runs, and says so in words of its own.
        var response = await app.NewClient().PostAsJsonAsync("/graphql", new { query = "mutation($i: LoginInput!) { login(input: $i) { id } }" });

        Assert.Contains("errors", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(Ours(app), e => e.Level >= LogLevel.Warning);
        Assert.False(app.Log.Mentions("LoginInput")); // the engine's text names the variable and its type: the log keeps only the codes
    }

    [Fact]
    public async Task TheOperation_IsTheOneAskedForByName_AndUnnamed_WhenTheRequestDoesNotSayWhichOfSeveral()
    {
        using var app = new TestApp(new() { ["Auth:Mode"] = "None" });
        var client = app.NewClient();
        const string two = "query Mine { myVehicles { id } } query Count { myVehicleCount }";

        await client.PostAsJsonAsync("/graphql", new { query = two, operationName = "Count" });
        var unsaid = await client.PostAsJsonAsync("/graphql", new { query = two }); // which of the two?

        Assert.Contains("errors", await unsaid.Content.ReadAsStringAsync());
        var timed = app.Log.From(Listener).Where(e => e.Values.ContainsKey("Ms")).Select(e => e.Values["Operation"]).ToArray();
        Assert.Equal<object?[]>(["query Count", "operation (unnamed)"], timed);
        Assert.DoesNotContain(Ours(app), e => e.Level >= LogLevel.Warning); // the client's doing
    }

    [Fact]
    public async Task AnOperationNameFromTheClient_NeverReachesTheLogUnchecked()
    {
        using var app = new TestApp(new() { ["Auth:Mode"] = "None" });
        var forged = "x\nwarn: forged"; // short, so only the check of its characters can turn it away

        var response = await app.NewClient().PostAsJsonAsync("/graphql", new { query = "query Mine { myVehicles { id } }", operationName = forged });

        Assert.Contains("errors", await response.Content.ReadAsStringAsync()); // answered with an error: there is no such operation
        Assert.DoesNotContain(Ours(app), e => e.Level >= LogLevel.Warning);
        Assert.False(app.Log.Mentions("forged"));
        var timed = Assert.Single(app.Log.From(Listener), e => e.Values.ContainsKey("Ms"));
        Assert.Equal("operation (invalid name)", timed.Values["Operation"]); // the name was seen, and turned away
    }
}

/// <summary>Fields only <see cref="LoggingTests"/> add to the schema (never the app's): a resolver whose request was aborted, and one that reports an error by its code.</summary>
[HotChocolate.Types.ExtendObjectType(HotChocolate.Types.OperationTypeNames.Query)]
public sealed class FaultyQueries
{
    public string Cancelled() => throw new OperationCanceledException();

    public string Refused() => throw new HotChocolate.GraphQLException(HotChocolate.ErrorBuilder.New().SetMessage("the words of the error").SetCode("TEST_REFUSED").Build());
}

/// <summary>Fields only <see cref="LoggingTests"/> add to the schema: values that must not be null but are, at the root and nested.</summary>
[HotChocolate.Types.ExtendObjectType(HotChocolate.Types.OperationTypeNames.Query)]
public sealed class NullQueries
{
    public string NullRoot() => null!;

    public NullHolder NullHolder() => new();
}

public sealed class NullHolder
{
    public string NullNested() => null!;
}
