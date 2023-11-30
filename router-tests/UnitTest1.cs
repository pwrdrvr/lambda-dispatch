using NUnit.Framework;
using System.Net.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using lambda_dispatch.router;

namespace lambda_dispatch.router.Tests
{
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

        [Test]
        public async Task TestRequestToPort5000()
        {
            _client.BaseAddress = new Uri("http://localhost:5000");
            var response = await _client.GetAsync("/");
            response.EnsureSuccessStatusCode();

            var responseString = await response.Content.ReadAsStringAsync();
            Assert.AreEqual("Hello World!", responseString);
        }

        [Test]
        public async Task TestRequestToPort5001()
        {
            _client.BaseAddress = new Uri("http://localhost:5001");
            var response = await _client.GetAsync("/control");
            response.EnsureSuccessStatusCode();

            var responseString = await response.Content.ReadAsStringAsync();
            Assert.AreEqual("Control Interface", responseString);
        }
    }
}