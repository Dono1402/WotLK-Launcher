using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WotLK.Launcher.Server;

public sealed class BrevoEmailClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly LauncherServerOptions _options;

    public BrevoEmailClient(HttpClient http, LauncherServerOptions options)
    {
        _http = http;
        _options = options;
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.BrevoApiKey)
        && !string.IsNullOrWhiteSpace(_options.BrevoSenderEmail);

    public async Task SendVerificationAsync(
        EmailVerificationChallenge challenge,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Brevo n'est pas configuré.");

        string verificationUrl =
            _options.PublicBaseUrl.TrimEnd('/')
            + "/api/v1/email/verify?token="
            + Uri.EscapeDataString(challenge.Token);
        string encodedName = WebUtility.HtmlEncode(challenge.Username);
        string encodedUrl = WebUtility.HtmlEncode(verificationUrl);
        string expiresAt = challenge.ExpiresAt
            .ToUniversalTime()
            .ToString("dd/MM/yyyy 'à' HH:mm 'UTC'");

        Dictionary<string, string>? headers = _options.BrevoSandbox
            ? new Dictionary<string, string>
            {
                ["X-Sib-Sandbox"] = "drop"
            }
            : null;

        var payload = new
        {
            sender = new
            {
                name = _options.BrevoSenderName,
                email = _options.BrevoSenderEmail
            },
            to = new[]
            {
                new
                {
                    name = challenge.Username,
                    email = challenge.Email
                }
            },
            subject = "Valide ton adresse e-mail Atlas",
            htmlContent = $$"""
                <!doctype html>
                <html lang="fr">
                <body style="margin:0;background:#0b1118;color:#e9edf2;font-family:Arial,sans-serif">
                  <div style="max-width:560px;margin:0 auto;padding:40px 24px">
                    <p style="margin:0 0 8px;color:#d6ad55;font-size:13px;font-weight:700;text-transform:uppercase">Atlas · Arthas</p>
                    <h1 style="margin:0 0 18px;font-size:28px">Valide ton adresse e-mail</h1>
                    <p style="margin:0 0 18px;line-height:1.6">Bonjour {{encodedName}},</p>
                    <p style="margin:0 0 26px;line-height:1.6">Confirme cette adresse pour compléter ton compte Atlas. Le téléchargement et le jeu restent disponibles pendant la validation.</p>
                    <p style="margin:0 0 26px">
                      <a href="{{encodedUrl}}" style="display:inline-block;padding:14px 22px;background:#c99a42;color:#0b1118;text-decoration:none;font-weight:700;border-radius:4px">VALIDER MON ADRESSE</a>
                    </p>
                    <p style="margin:0;color:#9ba6b2;font-size:13px;line-height:1.5">Ce lien expire le {{expiresAt}}. Si tu n'as pas demandé cet e-mail, tu peux simplement l'ignorer.</p>
                  </div>
                </body>
                </html>
                """,
            textContent = $"""
                Bonjour {challenge.Username},

                Valide ton adresse e-mail Atlas avec ce lien :
                {verificationUrl}

                Ce lien expire le {expiresAt}.
                Si tu n'as pas demandé cet e-mail, tu peux simplement l'ignorer.
                """,
            tags = new[] { "atlas-email-verification" },
            headers
        };

        using HttpRequestMessage request = new(HttpMethod.Post, "v3/smtp/email")
        {
            Content = JsonContent.Create(payload, options: JsonOptions)
        };
        request.Headers.Add("api-key", _options.BrevoApiKey);
        request.Headers.Accept.ParseAdd("application/json");

        using HttpResponseMessage response =
            await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
