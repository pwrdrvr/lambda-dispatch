using Moq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace PwrDrvr.LambdaDispatch.Router.Tests;

[TestFixture]
public class IncomingRequestMiddlewareTests
{
  private Mock<RequestDelegate> _mockNext;
  private Mock<IPoolManager> _mockPoolManager;
  private IncomingRequestMiddleware _middleware;
  private DefaultHttpContext _httpContext;

  [SetUp]
  public void SetUp()
  {
    _mockNext = new Mock<RequestDelegate>();
    _mockPoolManager = new Mock<IPoolManager>();
    _middleware = new IncomingRequestMiddleware(_mockNext.Object, _mockPoolManager.Object, new int[] { 5001, 5002 });
    _httpContext = new DefaultHttpContext();
    _httpContext.Response.Body = new MemoryStream();
    _httpContext.Features.Set<IHttpConnectionFeature>(new HttpConnectionFeature { LocalPort = 5001 });
  }

  [Test]
  public async Task InvokeAsync_HealthCheck_ReturnsOk()
  {
    _httpContext.Request.Path = "/health";

    await _middleware.InvokeAsync(_httpContext);

    Assert.That(_httpContext.Response.StatusCode, Is.EqualTo(200));
    _httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
    var responseBody = new StreamReader(_httpContext.Response.Body).ReadToEnd();
    Assert.That(responseBody, Is.EqualTo("OK"));
  }

  [Test]
  public async Task InvokeAsync_ValidLambdaName_AddsRequestToDispatcher()
  {
    _httpContext.Request.Headers["X-Lambda-Name"] = "testLambda";

    var mockDispatcher = new Mock<IDispatcher>();
    var mockPool = new Mock<IPool>();
    mockPool.Setup(p => p.Dispatcher).Returns(mockDispatcher.Object);
    IPool outPool = mockPool.Object;
    _mockPoolManager.Setup(m => m.GetOrCreatePoolByLambdaName(It.IsAny<string>())).Returns(mockPool.Object);

    await _middleware.InvokeAsync(_httpContext);

    mockDispatcher.Verify(m => m.AddRequest(_httpContext.Request, _httpContext.Response), Times.Once);
  }

  [Test]
  public async Task InvokeAsync_InvalidPort_CallsNext()
  {
    _httpContext.Features.Set<IHttpConnectionFeature>(new HttpConnectionFeature { LocalPort = 5003 });

    await _middleware.InvokeAsync(_httpContext);

    _mockNext.Verify(f => f.Invoke(_httpContext), Times.Once);
  }
}