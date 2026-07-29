using System.Net;

namespace WotLK.Launcher.Server;

public static class EmailVerificationPages
{
    public static string Confirmation(string token, string publicBaseUrl)
    {
        string action = WebUtility.HtmlEncode(
            publicBaseUrl.TrimEnd('/') + "/api/v1/email/verify");
        string encodedToken = WebUtility.HtmlEncode(token);

        return Page(
            "Confirmer l'adresse e-mail",
            "Une dernière étape",
            "Clique sur le bouton pour confirmer l'adresse e-mail de ton compte Atlas.",
            $"""
             <form method="post" action="{action}">
               <input type="hidden" name="token" value="{encodedToken}">
               <button type="submit">CONFIRMER MON ADRESSE</button>
             </form>
             """);
    }

    public static string Result(EmailVerificationResult result)
    {
        return result switch
        {
            EmailVerificationResult.Verified => Page(
                "Adresse validée",
                "Adresse e-mail validée",
                "Ton compte Atlas est maintenant à jour. Tu peux fermer cette page et revenir au launcher."),
            EmailVerificationResult.AlreadyVerified => Page(
                "Adresse déjà validée",
                "C'est déjà fait",
                "Cette adresse e-mail est déjà validée. Tu peux fermer cette page."),
            EmailVerificationResult.Expired => Page(
                "Lien expiré",
                "Ce lien a expiré",
                "Retourne dans le profil du launcher et demande un nouvel e-mail de validation."),
            _ => Invalid()
        };
    }

    public static string Invalid()
        => Page(
            "Lien invalide",
            "Lien invalide",
            "Ce lien de validation n'est pas valide. Demande un nouvel e-mail depuis le launcher.");

    private static string Page(
        string title,
        string heading,
        string message,
        string action = "")
    {
        return $$"""
            <!doctype html>
            <html lang="fr">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width,initial-scale=1">
              <title>{{WebUtility.HtmlEncode(title)}} · Atlas</title>
              <style>
                :root { color-scheme: dark; }
                * { box-sizing: border-box; }
                body { margin: 0; min-height: 100vh; display: grid; place-items: center; padding: 24px; background: #090e14; color: #e9edf2; font-family: Arial, sans-serif; }
                main { width: min(100%, 520px); padding: 34px; background: #151a20; border: 1px solid #3d4650; border-radius: 6px; box-shadow: 0 18px 55px #0008; }
                .eyebrow { margin: 0 0 10px; color: #d6ad55; font-size: 12px; font-weight: 700; text-transform: uppercase; }
                h1 { margin: 0 0 14px; font-size: 28px; }
                p { margin: 0; color: #b8c0ca; line-height: 1.6; }
                form { margin-top: 26px; }
                button { width: 100%; min-height: 48px; border: 1px solid #ebc36d; border-radius: 4px; background: #c99a42; color: #0b1118; font: inherit; font-weight: 800; cursor: pointer; }
                button:hover { background: #deb25c; }
              </style>
            </head>
            <body>
              <main>
                <p class="eyebrow">Atlas · Arthas</p>
                <h1>{{WebUtility.HtmlEncode(heading)}}</h1>
                <p>{{WebUtility.HtmlEncode(message)}}</p>
                {{action}}
              </main>
            </body>
            </html>
            """;
    }
}
