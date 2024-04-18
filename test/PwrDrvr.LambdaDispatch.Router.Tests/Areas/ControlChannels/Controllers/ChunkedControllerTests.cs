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
}