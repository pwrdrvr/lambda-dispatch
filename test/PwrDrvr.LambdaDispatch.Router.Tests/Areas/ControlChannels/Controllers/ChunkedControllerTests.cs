using Moq;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using PwrDrvr.LambdaDispatch.Router.ControlChannels.Controllers;
using PwrDrvr.LambdaDispatch.Router.EmbeddedMetrics;
using PwrDrvr.LambdaDispatch.Router.Tests.Mocks;
using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Http.Features;
using System.Security.Claims;

namespace PwrDrvr.LambdaDispatch.Router.ControlChannels.Tests;

public class ThrowingHttpContext : HttpContext
{
  private readonly ThrowingHttpResponse _response;
  private readonly DefaultHttpContext _defaultHttpContext;

  public ThrowingHttpContext()
  {
    _defaultHttpContext = new DefaultHttpContext();
    _response = new ThrowingHttpResponse(this);
  }

  public override HttpResponse Response => _response;

  public override IFeatureCollection Features => throw new NotImplementedException();

  public override HttpRequest Request => _defaultHttpContext.Request;

  public override ConnectionInfo Connection => throw new NotImplementedException();

  public override WebSocketManager WebSockets => throw new NotImplementedException();

  public override ClaimsPrincipal User { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
  public override IDictionary<object, object?> Items { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
  public override IServiceProvider RequestServices { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
  public override CancellationToken RequestAborted { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
  public override string TraceIdentifier { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
  public override ISession Session { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

  public override void Abort()
  {
    throw new NotImplementedException();
  }
}

public class ThrowingHttpResponse : HttpResponse
{
  private readonly HttpContext _context;

  public ThrowingHttpResponse(HttpContext context) : base()
  {
    _context = context;
  }

  public override HttpContext HttpContext => throw new NotImplementedException();

  public override int StatusCode { get => throw new NotImplementedException(); set { /* do nothing */ } }

  public override IHeaderDictionary Headers => throw new NotImplementedException();

  public override Stream Body { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
  public override long? ContentLength { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
  public override string? ContentType { get => throw new NotImplementedException(); set { /* do nothing */ } }

  public override IResponseCookies Cookies => throw new NotImplementedException();

  public override bool HasStarted => throw new NotImplementedException();

  public override void OnCompleted(Func<object, Task> callback, object state)
  {
    // Do Nothing
  }

  public override void OnStarting(Func<object, Task> callback, object state)
  {
    // Do Nothing
  }

  public override void Redirect([StringSyntax("Uri")] string location, bool permanent)
  {
    throw new NotImplementedException();
  }

  public override Task StartAsync(CancellationToken cancellationToken = default)
  {
    // You can make this method throw an exception or do anything else you need for your tests
    throw new Exception("StartAsync exception");
  }
}

[TestFixture]
public class ChunkedControllerTests
{
  private Mock<IPoolManager> mockPoolManager;
  private Mock<IMetricsLogger> mockMetricsLogger;
  private Mock<ILogger<ChunkedController>> mockLogger;
  private Mock<IMetricsRegistry> mockMetricsRegistry;
  private ChunkedController controller;

  [SetUp]
  public void SetUp()
  {
    mockPoolManager = new Mock<IPoolManager>();
    mockMetricsLogger = new Mock<IMetricsLogger>();
    mockLogger = new Mock<ILogger<ChunkedController>>();
    mockMetricsRegistry = MetricsRegistryMockFactory.Create();
    controller = new ChunkedController(mockPoolManager.Object, mockMetricsLogger.Object, mockLogger.Object, mockMetricsRegistry.Object);
  }

  [Test]
  public void PingInstance_DefaultPoolExists_ReturnsOk()
  {
    // Arrange
    var lambdaId = "testLambdaId";
    var mockPool = new Mock<IPool>();
    var mockDispatcher = new Mock<IDispatcher>();
    mockPool.Setup(p => p.Dispatcher).Returns(mockDispatcher.Object);
    IPool? outPool = mockPool.Object;
    mockPoolManager.Setup(p => p.GetPoolByPoolId("default", out outPool)).Returns(true);

    var httpContext = new DefaultHttpContext();
    httpContext.Request.Headers["X-Lambda-Id"] = lambdaId;
    controller.ControllerContext = new ControllerContext
    {
      HttpContext = httpContext
    };

    // Act
    var result = controller.PingInstance(lambdaId);

    // Assert
    Assert.That(result, Is.InstanceOf<OkResult>());
  }

  [Test]
  public void PingInstance_PoolExists_ReturnsOk()
  {
    // Arrange
    var poolId = "testPoolId";
    var lambdaId = "testLambdaId";
    var mockPool = new Mock<IPool>();
    var mockDispatcher = new Mock<IDispatcher>();
    mockPool.Setup(p => p.Dispatcher).Returns(mockDispatcher.Object);
    IPool? outPool = mockPool.Object;
    mockPoolManager.Setup(p => p.GetPoolByPoolId(poolId, out outPool)).Returns(true);

    // Act
    var result = controller.PingInstance(poolId, lambdaId);

    // Assert
    Assert.That(result, Is.InstanceOf<OkResult>());
  }

  [Test]
  public void PingInstance_PoolDoesNotExist_ReturnsNotFound()
  {
    // Arrange
    var poolId = "testPoolId";
    var lambdaId = "testLambdaId";
    IPool? outPool = null;
    mockPoolManager.Setup(p => p.GetPoolByPoolId(poolId, out outPool)).Returns(false);

    // Act
    var result = controller.PingInstance(poolId, lambdaId);

    // Assert
    Assert.That(result, Is.InstanceOf<NotFoundResult>());
  }

  [Test]
  public async Task CloseInstance_WhenPoolExists_ClosesInstanceAndReturnsOk()
  {
    // Arrange
    var poolId = "testPool";
    var lambdaId = "testLambda";
    var mockPool = new Mock<IPool>();
    var mockDispatcher = new Mock<IDispatcher>();
    IPool? outPool = mockPool.Object;
    mockPoolManager.Setup(p => p.GetPoolByPoolId(poolId, out outPool)).Returns(true);
    mockPool.Setup(x => x.Dispatcher).Returns(mockDispatcher.Object);
    mockPool.Setup(x => x.Dispatcher.CloseInstance(lambdaId, true)).Returns(Task.CompletedTask);

    // Act
    var result = await controller.CloseInstance(poolId, lambdaId);

    // Assert
    Assert.IsInstanceOf<OkResult>(result);
    mockPool.Verify(x => x.Dispatcher.CloseInstance(lambdaId, true), Times.Once);
  }

  [Test]
  public async Task CloseInstance_WhenPoolDoesNotExist_ReturnsNotFound()
  {
    // Arrange
    var poolId = "testPool";
    var lambdaId = "testLambda";
    mockPoolManager.Setup(x => x.GetPoolByPoolId(poolId, out It.Ref<IPool?>.IsAny)).Returns(false);

    // Act
    var result = await controller.CloseInstance(poolId, lambdaId);

    // Assert
    Assert.IsInstanceOf<NotFoundResult>(result);
  }

  [Test]
  public async Task CloseInstance_WithoutPoolId_UsesGetPoolIdAndClosesInstance()
  {
    // Arrange
    var poolId = "testPool";
    var lambdaId = "testLambda";
    var mockPool = new Mock<IPool>();
    var mockDispatcher = new Mock<IDispatcher>();
    mockDispatcher.Setup(x => x.CloseInstance(lambdaId, true)).Returns(Task.CompletedTask);
    IPool? outPool = mockPool.Object;
    mockPoolManager.Setup(p => p.GetPoolByPoolId(poolId, out outPool)).Returns(true);
    mockPool.Setup(x => x.Dispatcher).Returns(mockDispatcher.Object);

    var httpContext = new DefaultHttpContext();
    httpContext.Request.Headers["X-Pool-Id"] = poolId;

    controller.ControllerContext = new ControllerContext
    {
      HttpContext = httpContext
    };

    // Act
    var result = await controller.CloseInstance(lambdaId);

    // Assert
    Assert.IsInstanceOf<OkResult>(result);
    mockPool.Verify(x => x.Dispatcher.CloseInstance(lambdaId, true), Times.Once);
  }

  [Test]
  public async Task Post_WithLambdaIdAndChannelId_ReturnsOk()
  {
    // Arrange
    var lambdaId = "testLambda";
    var channelId = "testChannel";
    var mockPool = new Mock<IPool>();
    var mockConnection = new Mock<ILambdaConnection>();
    var tcs = new TaskCompletionSource();
    tcs.SetResult();
    mockConnection.Setup(x => x.TCS).Returns(tcs);
    var dispatcherResult = new DispatcherAddConnectionResult
    {
      Connection = mockConnection.Object,
      LambdaIDNotFound = false
    };
    var mockDispatcher = new Mock<IDispatcher>();
    mockDispatcher.Setup(x => x.AddConnectionForLambda(It.IsAny<HttpRequest>(), It.IsAny<HttpResponse>(), lambdaId, channelId))
                  .Returns(Task.FromResult(dispatcherResult));
    IPool? outPool = mockPool.Object;
    mockPoolManager.Setup(p => p.GetPoolByPoolId("default", out outPool)).Returns(true);
    mockPool.Setup(x => x.Dispatcher).Returns(mockDispatcher.Object);
    var httpContext = new DefaultHttpContext();
    httpContext.Request.Headers["X-Lambda-Id"] = lambdaId;
    controller.ControllerContext = new ControllerContext
    {
      HttpContext = httpContext
    };

    // Act
    await controller.Post(lambdaId, channelId);

    // Assert
    Assert.That(controller.Response.StatusCode, Is.EqualTo(200));
  }

  [Test]
  public async Task Post_WithPoolIdLambdaIdAndChannelId_ReturnsOk()
  {
    // Arrange
    var poolId = "testPool";
    var lambdaId = "testLambda";
    var channelId = "testChannel";
    var mockPool = new Mock<IPool>();
    var mockConnection = new Mock<ILambdaConnection>();
    var tcs = new TaskCompletionSource();
    tcs.SetResult();
    mockConnection.Setup(x => x.TCS).Returns(tcs);
    var dispatcherResult = new DispatcherAddConnectionResult
    {
      Connection = mockConnection.Object,
      LambdaIDNotFound = false
    };
    var mockDispatcher = new Mock<IDispatcher>();
    mockDispatcher.Setup(x => x.AddConnectionForLambda(It.IsAny<HttpRequest>(), It.IsAny<HttpResponse>(), lambdaId, channelId))
                  .Returns(Task.FromResult(dispatcherResult));
    IPool? outPool = mockPool.Object;
    mockPoolManager.Setup(p => p.GetPoolByPoolId(poolId, out outPool)).Returns(true);
    mockPool.Setup(x => x.Dispatcher).Returns(mockDispatcher.Object);
    var httpContext = new DefaultHttpContext();
    httpContext.Request.Headers["X-Lambda-Id"] = lambdaId;
    httpContext.Request.Headers["X-Pool-Id"] = poolId;
    controller.ControllerContext = new ControllerContext
    {
      HttpContext = httpContext
    };

    // Act
    await controller.Post(poolId, lambdaId, channelId);

    // Assert
    Assert.That(controller.Response.StatusCode, Is.EqualTo(200));
  }

  [Test]
  public async Task Post_WithPoolIdLambdaIdAndChannelId_ReturnsConflict()
  {
    // Arrange
    var poolId = "testPool";
    var lambdaId = "testLambda";
    var channelId = "testChannel";
    var mockPool = new Mock<IPool>();
    var mockConnection = new Mock<ILambdaConnection>();
    var tcs = new TaskCompletionSource();
    tcs.SetResult();
    mockConnection.Setup(x => x.TCS).Returns(tcs);
    var dispatcherResult = new DispatcherAddConnectionResult
    {
      Connection = null,
      LambdaIDNotFound = true
    };
    var mockDispatcher = new Mock<IDispatcher>();
    mockDispatcher.Setup(x => x.AddConnectionForLambda(It.IsAny<HttpRequest>(), It.IsAny<HttpResponse>(), lambdaId, channelId))
                  .Returns(Task.FromResult(dispatcherResult));
    IPool? outPool = mockPool.Object;
    mockPoolManager.Setup(p => p.GetPoolByPoolId(poolId, out outPool)).Returns(true);
    mockPool.Setup(x => x.Dispatcher).Returns(mockDispatcher.Object);
    var httpContext = new DefaultHttpContext();
    httpContext.Request.Headers["X-Lambda-Id"] = lambdaId;
    httpContext.Request.Headers["X-Pool-Id"] = poolId;
    controller.ControllerContext = new ControllerContext
    {
      HttpContext = httpContext
    };

    // Act
    await controller.Post(poolId, lambdaId, channelId);

    // Assert
    Assert.That(controller.Response.StatusCode, Is.EqualTo(409));
  }

  [Test]
  public void Post_LambdaIdNotFoundThrows_DoesNotThrow()
  {
    // Arrange
    var poolId = "testPool";
    var lambdaId = "testLambda";
    var channelId = "testChannel";
    var mockPool = new Mock<IPool>();
    var mockConnection = new Mock<ILambdaConnection>();
    var tcs = new TaskCompletionSource();
    tcs.SetResult();
    mockConnection.Setup(x => x.TCS).Returns(tcs);
    var dispatcherResult = new DispatcherAddConnectionResult
    {
      Connection = null,
      LambdaIDNotFound = true
    };
    var mockDispatcher = new Mock<IDispatcher>();
    mockDispatcher.Setup(x => x.AddConnectionForLambda(It.IsAny<HttpRequest>(), It.IsAny<HttpResponse>(), lambdaId, channelId))
                  .Returns(Task.FromResult(dispatcherResult));
    IPool? outPool = mockPool.Object;
    mockPoolManager.Setup(p => p.GetPoolByPoolId(poolId, out outPool)).Returns(true);
    mockPool.Setup(x => x.Dispatcher).Returns(mockDispatcher.Object);
    var httpContext = new ThrowingHttpContext();
    httpContext.Request.Headers["X-Lambda-Id"] = lambdaId;
    httpContext.Request.Headers["X-Pool-Id"] = poolId;
    controller.ControllerContext = new ControllerContext
    {
      HttpContext = httpContext
    };

    // Act
    // Asset no exception thrown
    Assert.DoesNotThrowAsync(async () => await controller.Post(poolId, lambdaId, channelId));
  }

  [Test]
  public async Task Post_PoolIdNotFound_ReturnsConflict()
  {
    // Arrange
    var poolId = "testPool";
    var lambdaId = "testLambda";
    var channelId = "testChannel";
    IPool? outPool = null;
    mockPoolManager.Setup(p => p.GetPoolByPoolId(poolId, out outPool)).Returns(false);
    var httpContext = new DefaultHttpContext();
    httpContext.Request.Headers["X-Lambda-Id"] = lambdaId;
    httpContext.Request.Headers["X-Pool-Id"] = poolId;
    httpContext.Request.Headers["Date"] = "Tue, 01 Feb 2022 12:34:56 GMT";
    controller.ControllerContext = new ControllerContext
    {
      HttpContext = httpContext
    };

    // Act
    await controller.Post(poolId, lambdaId, channelId);

    // Assert
    Assert.That(controller.Response.StatusCode, Is.EqualTo(409));
  }

  [Test]
  public async Task Post_PoolIdMultiNotFound_ReturnsConflict()
  {
    // Arrange
    var poolId = string.Empty;
    var lambdaId = "testLambda";
    var channelId = "testChannel";
    IPool? outPool = null;
    mockPoolManager.Setup(p => p.GetPoolByPoolId(poolId, out outPool)).Returns(false);
    var httpContext = new DefaultHttpContext();
    httpContext.Request.Headers["X-Lambda-Id"] = lambdaId;
    httpContext.Request.Headers["X-Pool-Id"] = poolId;
    httpContext.Request.Headers["Date"] = "Tue, 01 Feb 2022 12:34:56 GMT";
    controller.ControllerContext = new ControllerContext
    {
      HttpContext = httpContext
    };

    // Act
    await controller.Post(lambdaId, channelId);

    // Assert
    Assert.That(controller.Response.StatusCode, Is.EqualTo(409));
  }

  [Test]
  public async Task Post_NoXLambdaIdHeader_ReturnsBadRequest()
  {
    // Arrange
    var poolId = "testPool";
    var lambdaId = "testLambda";
    var channelId = "testChannel";
    IPool? outPool = null;
    mockPoolManager.Setup(p => p.GetPoolByPoolId(poolId, out outPool)).Returns(false);
    var httpContext = new DefaultHttpContext();
    httpContext.Request.Headers["X-Pool-Id"] = poolId;
    controller.ControllerContext = new ControllerContext
    {
      HttpContext = httpContext
    };

    // Act
    await controller.Post(poolId, lambdaId, channelId);

    // Assert
    Assert.That(controller.Response.StatusCode, Is.EqualTo(400));
  }
}