using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;

namespace PwrDrvr.LambdaDispatch.Router.Tests;

public class StartupTests
{
    private TestServer _server;
    private HttpClient _client;

    [SetUp]
    public void SetUp()
    {
        _server = new TestServer(new WebHostBuilder()
            .UseStartup<Startup>());
        _client = _server.CreateClient();
    }

    [TearDown]
    public void TearDown()
    {
        _client.Dispose();
        _server.Dispose();
    }

    [Test]
    [Ignore("This test is not working")]
    public async Task TestRequestToPort5001()
    {
        _client.BaseAddress = new Uri("http://localhost:5001");
        var response = await _client.GetAsync("/");
        response.EnsureSuccessStatusCode();

        var responseString = await response.Content.ReadAsStringAsync();
        Assert.That(responseString, Is.EqualTo("Hello World!"));
    }

    [Test]
    [Ignore("This test is not working")]
    public async Task TestRequestToPort5003()
    {
        _client.BaseAddress = new Uri("http://localhost:5003");
        var response = await _client.GetAsync("/api/chunked");
        response.EnsureSuccessStatusCode();

        var responseString = await response.Content.ReadAsStringAsync();
        Assert.That(responseString, Is.EqualTo("Control Interface"));
    }
}