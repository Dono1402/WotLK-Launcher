using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using WotLK.Launcher.Server;
using WotLK.Launcher.Server.Avatars;

internal static partial class AvatarBackendTests
{
    private static async Task ValidateEnrollmentBoundariesAsync(LauncherServerOptions options)
    {
        LauncherDatabase database = CreateDatabase(options);
        await ValidateEnrollmentInputRulesAsync();
        await ValidateWrongPasswordEnrollmentAsync(options, database);
        await ValidateTechnicalAccountEnrollmentAsync(options, database);
        AuthResponse existingAtlas = await RegisterIdentityAccountAsync(database, "ENROLLED");
        await ValidateAlreadyEnrolledAsync(database, existingAtlas);
        await ValidateUsedEmailEnrollmentAsync(options, database, existingAtlas.Profile.Email);
        await ValidateConcurrentEnrollmentAsync(options, database);
        await ValidateEnrollmentRollbackAsync(options, database);
        await ValidateSuccessfulEnrollmentCapabilitiesAsync(options, database, existingAtlas);
        await ValidateEnrollmentHttpContractAsync(options, database, existingAtlas.Profile.Email);
    }

    private static Task ValidateEnrollmentInputRulesAsync()
    {
        True(
            AuthenticationRequestValidation.ExistingEnrollment(
                new EnrollExistingAccountRequest(null!, IdentityPassword, "player@example.test"))
                == "Renseigne le nom de ton compte WoW.",
            "L'endpoint doit refuser un nom de compte JSON null sans erreur serveur.");
        True(
            AuthenticationRequestValidation.ExistingEnrollment(
                new EnrollExistingAccountRequest("Player", null!, "player@example.test"))
                == "Renseigne le mot de passe actuel de ton compte WoW.",
            "L'endpoint doit refuser un mot de passe JSON null sans erreur serveur.");
        True(
            AuthenticationRequestValidation.ExistingEnrollment(
                new EnrollExistingAccountRequest("Player", IdentityPassword, null!))
                == "Adresse e-mail invalide.",
            "L'endpoint doit refuser un e-mail JSON null sans erreur serveur.");
        True(
            AuthenticationRequestValidation.ExistingEnrollment(
                new EnrollExistingAccountRequest("Player", IdentityPassword, "invalid"))
                == "Adresse e-mail invalide.",
            "L'endpoint doit refuser un e-mail d'enrolement invalide.");
        True(
            AuthenticationRequestValidation.ExistingEnrollment(
                new EnrollExistingAccountRequest("Player", IdentityPassword, "player@example.test"))
                is null,
            "Le contrat d'enrolement valide ne doit pas etre rejete.");
        return Task.CompletedTask;
    }

    private static async Task ValidateWrongPasswordEnrollmentAsync(
        LauncherServerOptions options,
        LauncherDatabase database)
    {
        string username = IdentityUsername("ENROLLBAD");
        uint accountId = await InsertIdentityAzerothAccountAsync(
            options.ConnectionString,
            username,
            IdentityPassword);

        AtlasEnrollmentResult result = await database.EnrollExistingAsync(
            new EnrollExistingAccountRequest(
                username,
                IdentityPassword + "-wrong",
                $"{username.ToLowerInvariant()}@atlas.test"),
            "enrollment-wrong-password",
            CancellationToken.None);

        Equal(AtlasEnrollmentOutcome.InvalidCredentials, result.Outcome, "Un mauvais mot de passe doit etre refuse.");
        await AssertNoEnrollmentMaterialAsync(options.ConnectionString, accountId, username);
    }

    private static async Task ValidateTechnicalAccountEnrollmentAsync(
        LauncherServerOptions options,
        LauncherDatabase database)
    {
        string username = IdentityUsername("RNDBOT");
        uint accountId = await InsertIdentityAzerothAccountAsync(
            options.ConnectionString,
            username,
            IdentityPassword);

        AtlasEnrollmentResult result = await database.EnrollExistingAsync(
            new EnrollExistingAccountRequest(
                username,
                IdentityPassword,
                $"{username.ToLowerInvariant()}@atlas.test"),
            "enrollment-technical",
            CancellationToken.None);

        Equal(AtlasEnrollmentOutcome.NotEligible, result.Outcome, "Un compte technique connu ne doit pas etre enrolable.");
        await AssertNoEnrollmentMaterialAsync(options.ConnectionString, accountId, username);
    }

    private static async Task ValidateAlreadyEnrolledAsync(
        LauncherDatabase database,
        AuthResponse existingAtlas)
    {
        AtlasEnrollmentResult result = await database.EnrollExistingAsync(
            new EnrollExistingAccountRequest(
                existingAtlas.Profile.Username,
                IdentityPassword,
                $"other-{Guid.NewGuid():N}@example.test"),
            "enrollment-existing",
            CancellationToken.None);

        Equal(AtlasEnrollmentOutcome.AlreadyEnrolled, result.Outcome, "Un profil Atlas existant doit produire un conflit controle.");
    }

    private static async Task ValidateUsedEmailEnrollmentAsync(
        LauncherServerOptions options,
        LauncherDatabase database,
        string usedEmail)
    {
        string username = IdentityUsername("ENROLMAIL");
        uint accountId = await InsertIdentityAzerothAccountAsync(
            options.ConnectionString,
            username,
            IdentityPassword);

        AtlasEnrollmentResult result = await database.EnrollExistingAsync(
            new EnrollExistingAccountRequest(username, IdentityPassword, usedEmail),
            "enrollment-used-email",
            CancellationToken.None);

        Equal(AtlasEnrollmentOutcome.EmailAlreadyUsed, result.Outcome, "Un e-mail Atlas existant doit etre refuse.");
        await AssertNoEnrollmentMaterialAsync(options.ConnectionString, accountId, username);
    }

    private static async Task ValidateConcurrentEnrollmentAsync(
        LauncherServerOptions options,
        LauncherDatabase database)
    {
        string username = IdentityUsername("ENROLLRACE");
        uint accountId = await InsertIdentityAzerothAccountAsync(
            options.ConnectionString,
            username,
            IdentityPassword);
        EnrollExistingAccountRequest request = new(
            username,
            IdentityPassword,
            $"{username.ToLowerInvariant()}@atlas.test");

        AtlasEnrollmentResult[] results = await Task.WhenAll(
            database.EnrollExistingAsync(request, "enrollment-race-a", CancellationToken.None),
            database.EnrollExistingAsync(request, "enrollment-race-b", CancellationToken.None));

        Equal(1, results.Count(item => item.Outcome == AtlasEnrollmentOutcome.Succeeded), "Un seul enrolement concurrent doit reussir.");
        Equal(1, results.Count(item => item.Outcome == AtlasEnrollmentOutcome.AlreadyEnrolled), "Le second enrolement doit observer le profil cree.");
        await using MySqlConnection connection = new(options.ConnectionString);
        await connection.OpenAsync();
        Equal(1L, await IdentityCountAsync(connection, "SELECT COUNT(*) FROM atlas_launcher_profile WHERE account_id=@id", ("@id", accountId)), "Un seul profil doit exister.");
        Equal(1L, await IdentityCountAsync(connection, "SELECT COUNT(*) FROM atlas_launcher_session WHERE account_id=@id", ("@id", accountId)), "Un seul enrolement doit creer une session.");
    }

    private static async Task ValidateEnrollmentRollbackAsync(
        LauncherServerOptions options,
        LauncherDatabase database)
    {
        string username = IdentityUsername("ENROLLFAIL");
        uint accountId = await InsertIdentityAzerothAccountAsync(
            options.ConnectionString,
            username,
            IdentityPassword);
        const string triggerName = "atlas_enrollment_session_failure";
        await using MySqlConnection connection = new(options.ConnectionString);
        await connection.OpenAsync();
        string before = Convert.ToString(await EnrollmentScalarAsync(
            connection,
            "SELECT CONCAT(HEX(salt), ':', HEX(verifier), ':', email, ':', reg_mail) FROM account WHERE id=@id",
            ("@id", accountId))) ?? string.Empty;

        await using (MySqlCommand trigger = connection.CreateCommand())
        {
            trigger.CommandText = $"""
                DROP TRIGGER IF EXISTS `{triggerName}`;
                CREATE TRIGGER `{triggerName}`
                BEFORE INSERT ON atlas_launcher_session
                FOR EACH ROW
                BEGIN
                    IF NEW.device_name = 'enrollment-rollback' THEN
                        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'enrollment rollback test';
                    END IF;
                END;
                """;
            await trigger.ExecuteNonQueryAsync();
        }

        try
        {
            await ExpectAsync<MySqlException>(
                () => database.EnrollExistingAsync(
                    new EnrollExistingAccountRequest(
                        username,
                        IdentityPassword,
                        $"{username.ToLowerInvariant()}@atlas.test"),
                    "enrollment-rollback",
                    CancellationToken.None),
                "Une panne au milieu de la transaction doit remonter.");
        }
        finally
        {
            await using MySqlCommand drop = connection.CreateCommand();
            drop.CommandText = $"DROP TRIGGER IF EXISTS `{triggerName}`";
            await drop.ExecuteNonQueryAsync();
        }

        await AssertNoEnrollmentMaterialAsync(options.ConnectionString, accountId, username);
        string after = Convert.ToString(await EnrollmentScalarAsync(
            connection,
            "SELECT CONCAT(HEX(salt), ':', HEX(verifier), ':', email, ':', reg_mail) FROM account WHERE id=@id",
            ("@id", accountId))) ?? string.Empty;
        Equal(before, after, "Le compte WoW historique doit rester identique apres rollback.");
    }

    private static async Task ValidateSuccessfulEnrollmentCapabilitiesAsync(
        LauncherServerOptions options,
        LauncherDatabase database,
        AuthResponse existingAtlas)
    {
        string username = IdentityUsername("ENROLLGOOD");
        uint accountId = await InsertIdentityAzerothAccountAsync(
            options.ConnectionString,
            username,
            IdentityPassword);
        string email = $"{username.ToLowerInvariant()}@atlas.test";

        AtlasEnrollmentResult result = await database.EnrollExistingAsync(
            new EnrollExistingAccountRequest(username, IdentityPassword, email),
            "enrollment-success",
            CancellationToken.None);

        Equal(AtlasEnrollmentOutcome.Succeeded, result.Outcome, "Le compte joueur valide doit etre enrole.");
        AuthResponse response = result.Response ?? throw new InvalidOperationException("Session d'enrolement absente.");
        Equal(accountId, response.Profile.AccountId, "L'enrolement doit conserver exactement le meme account ID.");
        True(!response.Profile.EmailVerified, "L'e-mail d'enrolement doit commencer non verifie.");

        await using (MySqlConnection connection = new(options.ConnectionString))
        {
            await connection.OpenAsync();
            Equal(1L, await IdentityCountAsync(connection, "SELECT COUNT(*) FROM account WHERE id=@id", ("@id", accountId)), "Aucune seconde ligne account ne doit etre creee.");
            Equal(1L, await IdentityCountAsync(connection, "SELECT COUNT(*) FROM atlas_launcher_profile WHERE account_id=@id", ("@id", accountId)), "Le profil Atlas doit etre cree.");
            Equal(1L, await IdentityCountAsync(connection, "SELECT COUNT(*) FROM hermes_bnet_credentials WHERE BINARY username=BINARY @username", ("@username", username.ToUpperInvariant())), "Le credential moderne doit etre cree.");
            Equal(1L, await IdentityCountAsync(connection, "SELECT COUNT(*) FROM atlas_launcher_session WHERE account_id=@id", ("@id", accountId)), "La session initiale doit etre creee.");
        }

        AuthenticatedAccount? authenticated = await database.AuthenticateAsync(response.AccessToken, CancellationToken.None);
        Equal(accountId, authenticated?.AccountId, "La session d'enrolement doit etre utilisable.");
        AuthResponse? refreshed = await database.RefreshAsync(response.RefreshToken, CancellationToken.None);
        Equal(accountId, refreshed?.Profile.AccountId, "La session d'enrolement doit pouvoir etre rafraichie.");
        AtlasLoginResult login = await database.LoginAsync(
            new LoginRequest(username, IdentityPassword, "enrollment-login"),
            CancellationToken.None);
        Equal(AtlasLoginOutcome.Succeeded, login.Outcome, "Le login apres enrolement doit reussir.");

        AvatarRepository avatars = new(options);
        AvatarRateLimitDecision permit = await avatars.TryConsumeUploadPermitAsync(accountId, CancellationToken.None);
        True(permit.Allowed, "Les avatars doivent etre disponibles apres enrolement.");
        AvatarAssetRecord pending = await avatars.CreatePendingAsync(accountId, CancellationToken.None);
        Equal(accountId, pending.OwnerAccountId, "L'avatar doit appartenir au nouveau profil Atlas.");

        FriendRequestResult friend = await database.SendFriendRequestAsync(
            accountId,
            existingAtlas.Profile.Username,
            CancellationToken.None);
        Equal(FriendRequestOutcome.Requested, friend.Outcome, "Les amis doivent etre disponibles apres enrolement.");
    }

    private static async Task ValidateEnrollmentHttpContractAsync(
        LauncherServerOptions options,
        LauncherDatabase database,
        string usedEmail)
    {
        string wrongUsername = IdentityUsername("ENHTTPBAD");
        await InsertIdentityAzerothAccountAsync(options.ConnectionString, wrongUsername, IdentityPassword);
        string technicalUsername = IdentityUsername("RNDBOT");
        await InsertIdentityAzerothAccountAsync(options.ConnectionString, technicalUsername, IdentityPassword);
        string usedEmailUsername = IdentityUsername("ENHTTPMAIL");
        await InsertIdentityAzerothAccountAsync(options.ConnectionString, usedEmailUsername, IdentityPassword);
        string successUsername = IdentityUsername("ENHTTPOK");
        await InsertIdentityAzerothAccountAsync(options.ConnectionString, successUsername, IdentityPassword);

        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            ApplicationName = typeof(AvatarBackendTests).Assembly.FullName
        });
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(database);
        WebApplication app = builder.Build();
        app.MapPost("/api/v1/auth/enroll-existing", async (
            EnrollExistingAccountRequest request,
            LauncherDatabase db,
            CancellationToken cancellationToken) =>
        {
            string? validation = AuthenticationRequestValidation.ExistingEnrollment(request);
            return validation is not null
                ? Results.BadRequest(new { error = validation })
                : AuthenticationEndpointResults.FromEnrollment(
                    await db.EnrollExistingAsync(request, "enrollment-http", cancellationToken));
        });

        await app.StartAsync();
        try
        {
            IServer server = app.Services.GetRequiredService<IServer>();
            string address = server.Features.Get<IServerAddressesFeature>()?.Addresses.Single()
                ?? throw new InvalidOperationException("Adresse HTTP d'enrolement introuvable.");
            using HttpClient client = new() { BaseAddress = new Uri(address) };

            using HttpResponseMessage invalidEmail = await client.PostAsJsonAsync(
                "/api/v1/auth/enroll-existing",
                new EnrollExistingAccountRequest(successUsername, IdentityPassword, "invalid"));
            Equal(HttpStatusCode.BadRequest, invalidEmail.StatusCode, "L'e-mail invalide doit produire 400.");

            using HttpResponseMessage wrong = await client.PostAsJsonAsync(
                "/api/v1/auth/enroll-existing",
                new EnrollExistingAccountRequest(wrongUsername, IdentityPassword + "-wrong", "wrong@atlas.test"));
            Equal(HttpStatusCode.Unauthorized, wrong.StatusCode, "Le mauvais mot de passe doit produire 401.");

            using HttpResponseMessage technical = await client.PostAsJsonAsync(
                "/api/v1/auth/enroll-existing",
                new EnrollExistingAccountRequest(technicalUsername, IdentityPassword, "technical@atlas.test"));
            Equal(HttpStatusCode.Forbidden, technical.StatusCode, "Le compte technique doit produire un refus controle.");
            AtlasAuthErrorResponse? technicalError = await technical.Content.ReadFromJsonAsync<AtlasAuthErrorResponse>();
            Equal(AtlasAuthErrorCodes.EnrollmentNotAllowed, technicalError?.Code, "Le code public du refus est incorrect.");
            True(
                technicalError is not null
                && !technicalError.Error.Contains("bot", StringComparison.OrdinalIgnoreCase)
                && !technicalError.Error.Contains("technique", StringComparison.OrdinalIgnoreCase),
                "Le refus public ne doit reveler aucune classification interne.");

            using HttpResponseMessage duplicateEmail = await client.PostAsJsonAsync(
                "/api/v1/auth/enroll-existing",
                new EnrollExistingAccountRequest(usedEmailUsername, IdentityPassword, usedEmail));
            Equal(HttpStatusCode.Conflict, duplicateEmail.StatusCode, "L'e-mail utilise doit produire 409.");

            using HttpResponseMessage success = await client.PostAsJsonAsync(
                "/api/v1/auth/enroll-existing",
                new EnrollExistingAccountRequest(
                    successUsername,
                    IdentityPassword,
                    $"{successUsername.ToLowerInvariant()}@atlas.test"));
            Equal(HttpStatusCode.OK, success.StatusCode, "L'endpoint d'enrolement doit retourner la session initiale.");
            AuthResponse? session = await success.Content.ReadFromJsonAsync<AuthResponse>();
            Equal(successUsername, session?.Profile.Username, "Le contrat HTTP doit retourner l'identite enrolee.");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private static async Task AssertNoEnrollmentMaterialAsync(
        string connectionString,
        uint accountId,
        string username)
    {
        await using MySqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        Equal(1L, await IdentityCountAsync(connection, "SELECT COUNT(*) FROM account WHERE id=@id", ("@id", accountId)), "Le compte AzerothCore doit rester present.");
        Equal(0L, await IdentityCountAsync(connection, "SELECT COUNT(*) FROM atlas_launcher_profile WHERE account_id=@id", ("@id", accountId)), "Aucun profil partiel ne doit rester.");
        Equal(0L, await IdentityCountAsync(connection, "SELECT COUNT(*) FROM atlas_launcher_session WHERE account_id=@id", ("@id", accountId)), "Aucune session partielle ne doit rester.");
        Equal(0L, await IdentityCountAsync(connection, "SELECT COUNT(*) FROM hermes_bnet_credentials WHERE BINARY username=BINARY @username", ("@username", username.ToUpperInvariant())), "Aucun credential moderne partiel ne doit rester.");
    }

    private static async Task<object?> EnrollmentScalarAsync(
        MySqlConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return await command.ExecuteScalarAsync();
    }
}
