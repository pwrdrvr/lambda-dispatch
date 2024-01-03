using NUnit.Framework;
using Moq;
using Moq.Protected;
using System.Net;
using Microsoft.Extensions.Logging;

namespace PwrDrvr.LambdaDispatch.Extension.Tests;

public class HttpReverseRequesterTest
{
  [Test]
  [Ignore("Does not work yet")]
  public async Task GetRequest_ReturnsExpectedResult()
  {
    // Arrange
    var expectedUri = new Uri("https://test.com:5003/api/chunked");
    var expectedResponse = new HttpResponseMessage(HttpStatusCode.OK)
    {
      Content = new StringContent(@"GET / HTTP/1.1
Host: www.example.com
User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:89.0) Gecko/20100101 Firefox/89.0
Accept: text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8
Accept-Language: en-US,en;q=0.5
Accept-Encoding: gzip, deflate, br
Transfer-Encoding: chunked
Connection: keep-alive
Upgrade-Insecure-Requests: 1
Cache-Control: max-age=0

Hello cats!
"),
    };

    var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
    mockHttpMessageHandler.Protected()
          .Setup<Task<HttpResponseMessage>>(
              "SendAsync",
              ItExpr.IsAny<HttpRequestMessage>(),
              ItExpr.IsAny<CancellationToken>())
          .ReturnsAsync(expectedResponse)
          .Callback<HttpRequestMessage, CancellationToken>(async (request, cancellationToken) =>
          {
            await request.Content.CopyToAsync(Stream.Null, cancellationToken);
            request.Content.Dispose();

            // You can check the request here
            Assert.That(request.RequestUri, Is.EqualTo(expectedUri));
          });

    var mockLogger = new Mock<ILogger<HttpReverseRequester>>();

    var httpClient = new HttpClient(mockHttpMessageHandler.Object);
    var httpReverseRequester = new HttpReverseRequester("id", expectedUri.ToString(), httpClient, mockLogger.Object);

    // Act
    var result = await httpReverseRequester.GetRequest("channelId");

    Assert.That(result.Item1, Is.EqualTo((int)HttpStatusCode.OK));
  }
}