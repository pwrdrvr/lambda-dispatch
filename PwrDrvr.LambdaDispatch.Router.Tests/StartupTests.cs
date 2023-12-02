using NUnit.Framework;
using System.Net.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using PwrDrvr.LambdaDispatch.Router;
using NUnit.Framework;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;


namespace PwrDrvr.LambdaDispatch.Router.Tests
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
            Assert.That(responseString, Is.EqualTo("Hello World!"));
        }

        public async Task TestRequestToPort5001()
        {
            _client.BaseAddress = new Uri("http://localhost:5001");
            var response = await _client.GetAsync("/api/chunked");
            response.EnsureSuccessStatusCode();

            var responseString = await response.Content.ReadAsStringAsync();
            Assert.That(responseString, Is.EqualTo("Control Interface"));
        }
    }
}