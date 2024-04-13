using Moq;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Mvc;
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
}