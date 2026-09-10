using System.Net;
using System.Net.Http;
using System.Text.Json;
using WotLK.Launcher.Runtime;

internal static partial class ArmorySessionTests
{
    private static async Task ShopReadsGuardSessionAsync()
    {
        await using (Fixture success = await Fixture.CreateAsync())
        {
            success.Http.OnSend = (_, _) => Task.FromResult(ShopResponse());
            AuthSessionSnapshot current = success.Runtime.Session.CurrentSnapshot;
            Require((await success.Runtime.GetShopAsync(default)).Characters.Count == 2, "Authenticated shop snapshot is accepted.");
            AssertSessionUnchanged(success, current);
            Require(success.Http.Requests.SequenceEqual(["/api/v1/shop"]), "Shop has one trusted read route.");
        }
        foreach (bool rejectRefresh in new[] { false, true })
        {
            await using Fixture refusal = await Fixture.CreateAsync();
            if (rejectRefresh) refusal.Authentication.EnsureFreshHandler = _ => Task.FromResult(false);
            else refusal.Http.OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            await ThrowsAsync<UnauthorizedAccessException>(() => refusal.Runtime.GetShopAsync(default));
            AssertExpired(refusal);
        }
        foreach (uint nextAccount in new uint[] { 42, 84 })
        foreach (bool unauthorized in new[] { false, true })
        {
            await using Fixture fixture = await Fixture.CreateAsync();
            TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<HttpResponseMessage> reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Http.OnSend = (_, _) => { entered.SetResult(); return reply.Task; };
            Task pending = fixture.Runtime.GetShopAsync(default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            AuthSessionSnapshot connected = await fixture.ReconnectAsync(nextAccount);
            reply.SetResult(unauthorized ? new(HttpStatusCode.Unauthorized) : ShopResponse());
            await ThrowsAsync<UnauthorizedAccessException>(() => pending);
            AssertSessionUnchanged(fixture, connected, nextAccount);
        }
        await using (Fixture cancelled = await Fixture.CreateAsync())
        {
            AuthSessionSnapshot before = cancelled.Runtime.Session.CurrentSnapshot;
            using CancellationTokenSource cancellation = new();
            cancelled.Http.OnSend = (_, _) => { cancellation.Cancel(); return Task.FromResult(ShopResponse()); };
            await ThrowsAsync<OperationCanceledException>(() => cancelled.Runtime.GetShopAsync(cancellation.Token));
            AssertSessionUnchanged(cancelled, before);
        }
        Console.WriteLine("Shop session PASS: current 401/refresh rejection, same-account and different-account reconnect, late success/401 and cancellation. Fake authentication and HTTP only.");
    }

    private static HttpResponseMessage ShopResponse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(ShopRuntimeTests.Snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web)))
    };
}
