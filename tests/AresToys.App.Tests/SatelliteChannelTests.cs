using System.Text.Json;
using AresToys.App.Services.Satellite;
using Xunit;

namespace AresToys.App.Tests;

public class SatelliteChannelTests
{
    private static string UniquePipe() => "AresToys.Test." + Guid.NewGuid().ToString("N");

    private static JsonElement Text(string value) => SatelliteProtocol.ToElement(new { text = value });

    private static string? TextOf(SatelliteMessage? message)
        => message is { Ok: true, Data: { } data } ? data.GetProperty("text").GetString() : null;

    /// <summary>A started server whose channel is given to <paramref name="onConnected"/> before it reads, and the
    /// connected client channel.</summary>
    private static async Task<(SatelliteServer Server, SatelliteChannel Client, SatelliteChannel Main)> ConnectAsync(
        string pipe, Action<SatelliteChannel>? onMain = null, Action<SatelliteChannel>? onClient = null)
    {
        var server = new SatelliteServer(null, pipe);
        var mainSide = new TaskCompletionSource<SatelliteChannel>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Connected += channel =>
        {
            onMain?.Invoke(channel);
            mainSide.TrySetResult(channel);
        };
        server.Start();

        var client = await SatelliteClient.ConnectAsync(onClient, 5000, pipe);
        Assert.NotNull(client);
        var main = await mainSide.Task.WaitAsync(TimeSpan.FromSeconds(5));
        return (server, client!, main);
    }

    [Fact]
    public async Task Request_IsAnsweredInBothDirections()
    {
        var (server, client, main) = await ConnectAsync(UniquePipe(),
            onMain: c => c.Handle("echo", data => Task.FromResult<JsonElement?>(Text("main:" + data!.Value.GetProperty("text").GetString()))),
            onClient: c => c.Handle("echo", data => Task.FromResult<JsonElement?>(Text("client:" + data!.Value.GetProperty("text").GetString()))));
        using (server)
        using (client)
        {
            Assert.Equal("main:a", TextOf(await client.RequestAsync("echo", Text("a"))));
            Assert.Equal("client:b", TextOf(await main.RequestAsync("echo", Text("b"))));
        }
    }

    [Fact]
    public async Task UnknownRequest_AnswersNotOk_AndThrowingHandlerCarriesItsMessage()
    {
        var (server, client, _) = await ConnectAsync(UniquePipe(),
            onMain: c => c.Handle("boom", _ => throw new InvalidOperationException("kaput")));
        using (server)
        using (client)
        {
            var unknown = await client.RequestAsync("nope");
            Assert.True(unknown is { Ok: false, Error: "Unknown request" });

            var boom = await client.RequestAsync("boom");
            Assert.True(boom is { Ok: false, Error: "kaput" });
        }
    }

    [Fact]
    public async Task ConcurrentRequests_AreAnsweredToTheRightCallers()
    {
        var (server, client, _) = await ConnectAsync(UniquePipe(),
            onMain: c => c.Handle("echo", async data =>
            {
                var text = data!.Value.GetProperty("text").GetString()!;
                // The first request is the slowest, so the answers come back in the opposite order.
                await Task.Delay(text == "0" ? 300 : 10);
                return Text(text);
            }));
        using (server)
        using (client)
        {
            var calls = Enumerable.Range(0, 8).Select(i => client.RequestAsync("echo", Text(i.ToString(System.Globalization.CultureInfo.InvariantCulture)), 5000)).ToArray();
            var answers = await Task.WhenAll(calls);
            for (var i = 0; i < answers.Length; i++) Assert.Equal(i.ToString(System.Globalization.CultureInfo.InvariantCulture), TextOf(answers[i]));
        }
    }

    [Fact]
    public async Task Publish_RaisesEventReceivedOnTheOtherSide_WithNameAndData_InOrder()
    {
        var fromClient = new List<string>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, client, main) = await ConnectAsync(UniquePipe(), onMain: c => c.EventReceived += (name, data) =>
        {
            lock (fromClient) fromClient.Add(name + ":" + data!.Value.GetProperty("text").GetString());
            if (name == "e3") done.TrySetResult();
        });
        using (server)
        using (client)
        {
            Assert.True(client.Publish("e1", Text("x")));
            Assert.True(client.Publish("e2", Text("y")));
            Assert.True(client.Publish("e3", Text("z")));
            await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
            lock (fromClient) Assert.Equal(["e1:x", "e2:y", "e3:z"], fromClient);

            var back = new TaskCompletionSource<(string, JsonElement?)>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.EventReceived += (name, data) => back.TrySetResult((name, data));
            Assert.True(main.Publish("ping", Text("w")));
            var (backName, backData) = await back.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("ping", backName);
            Assert.Equal("w", backData!.Value.GetProperty("text").GetString());
        }
    }

    [Fact]
    public async Task Request_ReturnsNull_OnTimeout_AndWhenTheChannelIsClosed()
    {
        var (server, client, _) = await ConnectAsync(UniquePipe(),
            onMain: c => c.Handle("never", _ => new TaskCompletionSource<JsonElement?>().Task));
        using (server)
        {
            Assert.Null(await client.RequestAsync("never", null, 200));

            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Closed += () => closed.TrySetResult();
            client.Dispose();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(client.IsOpen);
            Assert.Null(await client.RequestAsync("never", null, 200));
            Assert.False(client.Publish("e"));
        }
    }

    [Fact]
    public async Task PendingRequest_ReturnsNull_WhenThePeerDrops()
    {
        var (server, client, main) = await ConnectAsync(UniquePipe(),
            onMain: c => c.Handle("never", _ => new TaskCompletionSource<JsonElement?>().Task));
        using (server)
        using (client)
        {
            var pending = client.RequestAsync("never", null, 10000);
            await Task.Delay(100);
            main.Dispose();
            Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public async Task Closed_IsRaisedWhenTheClientDrops_AndTheServerAcceptsANewClient()
    {
        var pipe = UniquePipe();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, client, main) = await ConnectAsync(pipe, onMain: c => c.Handle("who", _ => Task.FromResult<JsonElement?>(Text("main"))));
        using (server)
        {
            main.Closed += () => closed.TrySetResult();
            client.Dispose();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(main.IsOpen);

            using var second = await SatelliteClient.ConnectAsync(5000, pipe);
            Assert.NotNull(second);
            Assert.Equal("main", TextOf(await second!.RequestAsync("who")));
        }
    }

    [Fact]
    public async Task NewClient_ReplacesAnOlderOneThatIsStillOpen()
    {
        var pipe = UniquePipe();
        var (server, first, firstMain) = await ConnectAsync(pipe, onMain: c => c.Handle("who", _ => Task.FromResult<JsonElement?>(Text("main"))));
        using (server)
        using (first)
        {
            using var second = await SatelliteClient.ConnectAsync(5000, pipe);
            Assert.NotNull(second);
            Assert.Equal("main", TextOf(await second!.RequestAsync("who")));
            Assert.False(firstMain.IsOpen);
            Assert.False(ReferenceEquals(firstMain, server.Current));
        }
    }

    [Fact]
    public async Task Client_ReturnsNull_WhenNobodyListens()
    {
        Assert.Null(await SatelliteClient.ConnectAsync(400, UniquePipe()));
    }

    [Fact]
    public void Args_ParseModulesAndMainPid()
    {
        Assert.True(SatelliteArgs.TryParse(["--satellite", "--modules=Wormholes, launcher", "--main-pid=123"], out var parsed));
        Assert.Equal(["launcher", "wormholes"], parsed!.Modules.Order());
        Assert.Equal(123, parsed.MainPid);

        Assert.False(SatelliteArgs.TryParse(["--modules=wormholes", "--main-pid=1"], out var none));
        Assert.Null(none);

        Assert.True(SatelliteArgs.TryParse(["--satellite"], out var bare));
        Assert.Empty(bare!.Modules);
        Assert.Equal(0, bare.MainPid);
    }

    [Fact]
    public void Guard_LetsASatelliteLaunchThrough_WithoutTakingTheMutex()
    {
        using var satellite = new AresToys.App.Services.SingleInstanceGuard(["--satellite", "--modules=launcher"]);
        Assert.True(satellite.IsPrimary);
        satellite.StartListening();
    }
}
