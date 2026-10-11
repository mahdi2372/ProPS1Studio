using System.Net;
using System.Text;
using NUnit.Framework;
using ProPS1Studio.Core.Auth;

namespace ProPS1Studio.Core.Tests;

[TestFixture]
public class KeyAuthClientTests
{
    private const string OwnerId = "dh97gCHBD8";
    private const string GoodBody = "{\"success\":true,\"sessionid\":\"s1\",\"ownerid\":\"" + OwnerId + "\"}";

    private FakeHandler _handler = null!;
    private KeyAuthClient _client = null!;

    [SetUp]
    public void SetUp()
    {
        _handler = new FakeHandler();
        _client = new KeyAuthClient(Config(), null, _handler);
    }

    [TearDown]
    public void TearDown() => _client.Dispose();

    private static KeyAuthConfig Config() => new()
    {
        Name = "me",
        OwnerId = OwnerId,
        Version = "1.0",
        Endpoint = "https://keyauth.win/api/1.3/",
    };

    [Test]
    public async Task InitAsync_Success_SetsSession()
    {
        _handler.EnqueueJson(GoodBody);

        var result = await _client.InitAsync();

        Assert.That(result.Success, Is.True);
        Assert.That(_client.IsInitialized, Is.True);
        Assert.That(_handler.RequestBodies, Has.Count.EqualTo(1));
        Assert.That(_handler.RequestBodies[0], Does.Contain("type=init"));
        Assert.That(_handler.RequestBodies[0], Does.Contain("name=me"));
        Assert.That(_handler.RequestBodies[0], Does.Contain("ownerid=" + OwnerId));
        Assert.That(_handler.RequestBodies[0], Does.Contain("hash="));
    }

    [Test]
    public async Task InitAsync_OwnerIdMismatch_Fails()
    {
        _handler.EnqueueJson("{\"success\":true,\"sessionid\":\"s1\",\"ownerid\":\"WRONGOWNR\"}");

        var result = await _client.InitAsync();

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("owner id"));
        Assert.That(_client.IsInitialized, Is.False, "mismatched response must not establish a session");
    }

    [Test]
    public async Task InitAsync_InvalidVer_FailsWithDownloadLink()
    {
        _handler.EnqueueJson(
            "{\"success\":false,\"message\":\"invalidver\",\"download\":\"https://keyauth.win/dl.php\",\"ownerid\":\"" + OwnerId + "\"}");

        var result = await _client.InitAsync();

        Assert.That(result.Success, Is.False);
        Assert.That(result.DownloadUrl, Is.EqualTo("https://keyauth.win/dl.php"));
        Assert.That(result.Message, Does.Contain("latest version"));
    }

    [Test]
    public async Task LoginAsync_AutoInits_ThenLogsIn()
    {
        _handler.EnqueueJson(GoodBody);
        _handler.EnqueueJson(
            "{\"success\":true,\"sessionid\":\"s2\",\"ownerid\":\"" + OwnerId + "\",\"info\":{\"username\":\"alice\"}}");

        var result = await _client.LoginAsync("alice", "pw");

        Assert.That(result.Success, Is.True);
        Assert.That(_handler.RequestBodies, Has.Count.EqualTo(2));
        Assert.That(_handler.RequestBodies[0], Does.Contain("type=init"));
        Assert.That(_handler.RequestBodies[1], Does.Contain("type=login"));
        Assert.That(_handler.RequestBodies[1], Does.Contain("sessionid=s1"));
        Assert.That(_handler.RequestBodies[1], Does.Contain("username=alice"));
        Assert.That(_client.Username, Is.EqualTo("alice"));
    }

    [Test]
    public async Task LoginAsync_WrongPassword_PassesServerMessage()
    {
        _handler.EnqueueJson(GoodBody);
        _handler.EnqueueJson(
            "{\"success\":false,\"message\":\"invalid username or password\",\"ownerid\":\"" + OwnerId + "\"}");

        var result = await _client.LoginAsync("alice", "nope");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Is.EqualTo("invalid username or password"));
        Assert.That(_client.Username, Is.Null);
    }

    [Test]
    public async Task RegisterAsync_Success_ReturnsUsername()
    {
        _handler.EnqueueJson(GoodBody);
        _handler.EnqueueJson(
            "{\"success\":true,\"sessionid\":\"s3\",\"ownerid\":\"" + OwnerId + "\",\"info\":{\"username\":\"bob\"}}");

        var result = await _client.RegisterAsync("bob", "pw", "LIC-KEY", "bob@example.com");

        Assert.That(result.Success, Is.True);
        Assert.That(result.Username, Is.EqualTo("bob"));
        Assert.That(_handler.RequestBodies[1], Does.Contain("type=register"));
        Assert.That(_handler.RequestBodies[1], Does.Contain("email=bob%40example.com"));
        Assert.That(_handler.RequestBodies[1], Does.Contain("key=LIC-KEY"));
    }

    [Test]
    public async Task RegisterAsync_Taken_PassesServerMessage()
    {
        _handler.EnqueueJson(GoodBody);
        _handler.EnqueueJson(
            "{\"success\":false,\"message\":\"user already exists\",\"ownerid\":\"" + OwnerId + "\"}");

        var result = await _client.RegisterAsync("bob", "pw", "LIC-KEY", "bob@example.com");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Is.EqualTo("user already exists"));
    }

    [Test]
    public async Task LicenseAsync_Failure_PassesServerMessage()
    {
        _handler.EnqueueJson(GoodBody);
        _handler.EnqueueJson(
            "{\"success\":false,\"message\":\"invalid license key\",\"ownerid\":\"" + OwnerId + "\"}");

        var result = await _client.LicenseAsync("BAD-KEY");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Is.EqualTo("invalid license key"));
        Assert.That(_handler.RequestBodies[1], Does.Contain("type=license"));
    }

    [Test]
    public async Task Unconfigured_FailsWithoutNetworkCall()
    {
        var client = new KeyAuthClient(new KeyAuthConfig(), null, _handler);
        using (client)
        {
            var result = await client.LoginAsync("a", "b");

            Assert.That(result.Success, Is.False);
            Assert.That(result.Message, Does.Contain("not configured"));
        }

        Assert.That(_handler.RequestBodies, Is.Empty);
    }

    [Test]
    public async Task NetworkError_ReturnsFriendlyMessage()
    {
        _handler.EnqueueException(new HttpRequestException("connection refused"));

        var result = await _client.InitAsync();

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("Could not reach KeyAuth"));
    }

    [Test]
    public async Task RateLimited_ReturnsFriendlyMessage()
    {
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.TooManyRequests));

        var result = await _client.InitAsync();

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("Too many requests"));
    }

    [Test]
    public async Task MalformedJson_FailsGracefully()
    {
        _handler.EnqueueJson("{\"success\":true,");

        var result = await _client.InitAsync();

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("unexpected response"));
        Assert.That(_client.IsInitialized, Is.False);
    }

    [Test]
    public async Task KeyAuthInvalidBody_ReportsAppNotFound()
    {
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("KeyAuth_Invalid", Encoding.UTF8, "text/plain"),
        });

        var result = await _client.InitAsync();

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("not found"));
    }

    [Test]
    public async Task ServerError_FailureMessageIsSurfaced()
    {
        _handler.EnqueueJson(GoodBody);
        _handler.EnqueueJson("{\"success\":false,\"message\":\"session expired\",\"ownerid\":\"" + OwnerId + "\"}");

        var result = await _client.LicenseAsync("LIC");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Is.EqualTo("session expired"));
    }

    [Test]
    public async Task LogoutAsync_ClearsLocalSession()
    {
        _handler.EnqueueJson(GoodBody);
        await _client.InitAsync();
        Assert.That(_client.IsInitialized, Is.True);

        _handler.EnqueueJson("{\"success\":true,\"sessionid\":\"s1\",\"ownerid\":\"" + OwnerId + "\"}");
        await _client.LogoutAsync();

        Assert.That(_client.IsInitialized, Is.False);
        Assert.That(_client.Username, Is.Null);
        Assert.That(_handler.RequestBodies, Has.Count.EqualTo(2));
        Assert.That(_handler.RequestBodies[1], Does.Contain("type=logout"));
    }

    [Test]
    public async Task LogoutAsync_NetworkFailure_StillClearsLocalSession()
    {
        _handler.EnqueueJson(GoodBody);
        await _client.InitAsync();

        _handler.EnqueueException(new HttpRequestException("down"));
        await _client.LogoutAsync();

        Assert.That(_client.IsInitialized, Is.False, "logout is best effort but local state must clear");
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responders = new();

        public List<string> RequestBodies { get; } = new();

        public void Enqueue(Func<HttpResponseMessage> responder) => _responders.Enqueue(responder);

        public void Enqueue(HttpResponseMessage response) => Enqueue(() => response);

        public void EnqueueJson(string json) => Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });

        public void EnqueueException(Exception ex) => Enqueue(() => throw ex);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));

            if (_responders.Count == 0)
            {
                throw new InvalidOperationException("No responder queued for this request.");
            }

            return _responders.Dequeue()();
        }
    }
}
