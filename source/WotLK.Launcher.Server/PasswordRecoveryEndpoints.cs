using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;

namespace WotLK.Launcher.Server;

internal sealed record PasswordRecoveryRequest(string? Email);
internal sealed record PasswordRecoveryConfirmation(string? Token, string? Password, string? Confirmation);

internal static class PasswordRecoveryEndpoints
{
    internal static void MapPasswordRecovery(this WebApplication app)
    {
        app.MapPost("/api/v1/auth/password-reset/request", (
            PasswordRecoveryRequest request, PasswordRecoveryService recovery, HttpContext context) =>
        {
            AuthenticationResponseHeaders.Apply(context.Response);
            if (string.IsNullOrWhiteSpace(request.Email) || request.Email.Trim().Length > 254
                || !new EmailAddressAttribute().IsValid(request.Email.Trim()))
                return Results.BadRequest(new { error = "Adresse e-mail invalide." });
            return recovery.TryQueue(request.Email) ? Results.StatusCode(202) : Results.StatusCode(503);
        }).RequireRateLimiting(AuthenticationRateLimiting.PasswordRecovery);

        app.MapGet("/api/v1/auth/password-reset", (HttpContext context) =>
        {
            AuthenticationResponseHeaders.Apply(context.Response);
            string nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
            context.Response.Headers.ContentSecurityPolicy = $"default-src 'none'; script-src 'nonce-{nonce}'; style-src 'nonce-{nonce}'; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.Content(PasswordRecoveryPage.Render(nonce), "text/html; charset=utf-8");
        });

        app.MapPost("/api/v1/auth/password-reset/confirm", async (
            PasswordRecoveryConfirmation request, LauncherDatabase database, HermesTicketClient hermes,
            HttpContext context, ILoggerFactory loggers, CancellationToken token) =>
        {
            AuthenticationResponseHeaders.Apply(context.Response);
            if (!database.PasswordRecoveryAvailable) return Results.StatusCode(503);
            if (request.Password is null || request.Password.Length is < 10 or > 128
                || !string.Equals(request.Password, request.Confirmation, StringComparison.Ordinal))
                return Results.BadRequest(new { error = "Vérifie le mot de passe et sa confirmation (10 à 128 caractères)." });
            string? username = await database.ResetPasswordAsync(request.Token, request.Password, token);
            if (username is null) return Results.BadRequest(new { error = "Ce lien est invalide ou expiré. Demande un nouveau lien depuis Atlas Launcher." });
            try
            {
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
                await hermes.RevokeAsync(username, timeout.Token);
            }
            catch { loggers.CreateLogger("PasswordRecovery").LogWarning("Hermes session revocation failed after a password reset."); }
            return Results.Ok(new { message = "Mot de passe modifié. Tu peux retourner dans Atlas Launcher et te connecter." });
        }).RequireRateLimiting(AuthenticationRateLimiting.PasswordRecoveryConfirm);
    }
}

internal static class PasswordRecoveryPage
{
    internal static string Render(string nonce) => $$"""
        <!doctype html><html lang="fr"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>Nouveau mot de passe · Atlas</title>
        <style nonce="{{nonce}}">
        *{box-sizing:border-box}body{margin:0;background:#091723;color:#edf7ff;font:16px system-ui,sans-serif;min-height:100vh;display:grid;place-items:center;padding:24px}
        main{width:100%;max-width:480px}h1{font-size:30px}p{line-height:1.6;color:#c5d6e2}label{display:block;margin-top:20px}
        input,button{font:inherit;width:100%;padding:14px;border:1px solid #688696;border-radius:6px;margin-top:8px}
        input{background:#102533;color:#fff}button{background:#79d7ed;color:#071822;cursor:pointer;font-weight:600;margin-top:24px}
        :focus-visible{outline:2px solid #a7eeff;outline-offset:3px}button:disabled{opacity:.6;cursor:wait}.error{color:#ffc6c6}.success{color:#a5ead2}
        </style><main><p>ATLAS LAUNCHER</p><h1>Choisis un nouveau mot de passe</h1><p>10 à 128 caractères. Une fois modifié, reconnecte-toi dans Atlas Launcher.</p>
        <form id="reset"><label for="password">Nouveau mot de passe</label><input id="password" type="password" minlength="10" maxlength="128" autocomplete="new-password" required>
        <label for="confirmation">Confirmer le mot de passe</label><input id="confirmation" type="password" minlength="10" maxlength="128" autocomplete="new-password" required>
        <button id="submit">Enregistrer le mot de passe</button></form><p id="message" role="status" aria-live="polite"></p></main>
        <script nonce="{{nonce}}">
        const token=new URLSearchParams(location.hash.slice(1)).get('token');
        history.replaceState(null,'',location.pathname);
        const form=document.getElementById('reset'),message=document.getElementById('message'),button=document.getElementById('submit'),password=document.getElementById('password'),confirmation=document.getElementById('confirmation');
        function show(text,ok=false){message.textContent=text;message.className=ok?'success':'error'}
        if(!token){form.hidden=true;show('Ce lien est incomplet. Demande un nouveau lien depuis Atlas Launcher.')}
        confirmation.addEventListener('input',()=>confirmation.setCustomValidity(''));
        password.addEventListener('input',()=>confirmation.setCustomValidity(''));
        form.addEventListener('submit',async event=>{event.preventDefault();
          if(password.value!==confirmation.value){confirmation.setCustomValidity('Les deux mots de passe ne correspondent pas.');confirmation.reportValidity();return}
          button.disabled=true;show('Modification en cours…',true);
          try{const response=await fetch(location.pathname.replace(/\/$/,'')+'/confirm',{method:'POST',credentials:'omit',headers:{'Content-Type':'application/json'},body:JSON.stringify({token,password:password.value,confirmation:confirmation.value}),signal:AbortSignal.timeout(20000)});
            if(response.ok){form.reset();form.hidden=true;show('Mot de passe modifié. Retourne dans Atlas Launcher pour te connecter avec ton nouveau mot de passe.',true)}
            else if(response.status===429)show('Trop de tentatives. Patiente une minute avant de réessayer.');
            else if(response.status===400){const body=await response.json();show(body.error||'Ce lien est invalide ou expiré.')}
            else show('Service temporairement indisponible. Réessaie plus tard.');
          }catch{show('Connexion interrompue. Si le mot de passe a déjà été modifié, utilise-le dans Atlas Launcher ; sinon, réessaie ou demande un nouveau lien.')}
          finally{button.disabled=false}
        });
        </script></html>
        """;
}
