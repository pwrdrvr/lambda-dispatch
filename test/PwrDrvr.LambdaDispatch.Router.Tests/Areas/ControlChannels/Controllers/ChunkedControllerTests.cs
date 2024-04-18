using Moq;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using PwrDrvr.LambdaDispatch.Router.ControlChannels.Controllers;
using PwrDrvr.LambdaDispatch.Router.EmbeddedMetrics;

namespace PwrDrvr.LambdaDispatch.Router.Tests;

[TestFixture]
public class ChunkedControllerTests
{
  private Mock<IPoolManager> mockPoolManager;
  private Mock<IMetricsLogger> mockMetricsLogger;
  private Mock<ILogger<ChunkedController>> mockLogger;
  private ChunkedController controller;

  [SetUp]
  public void SetUp()
  {
    mockPoolManager = new Mock<IPoolManager>();
    mockMetricsLogger = new Mock<IMetricsLogger>();
    mockLogger = new Mock<ILogger<ChunkedController>>();
    controller = new ChunkedController(mockPoolManager.Object, mockMetricsLogger.Object, mockLogger.Object);
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
    IPool outPool = mockPool.Object;
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
    IPool outPool = null;
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
    IPool outPool = mockPool.Object;
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
    mockPoolManager.Setup(x => x.GetPoolByPoolId(poolId, out It.Ref<IPool>.IsAny)).Returns(false);

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
    IPool outPool = mockPool.Object;
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
    IPool outPool = mockPool.Object;
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
    Assert.AreEqual(200, controller.Response.StatusCode);
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
    IPool outPool = mockPool.Object;
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
    Assert.AreEqual(200, controller.Response.StatusCode);
  }
}