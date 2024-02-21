namespace PwrDrvr.LambdaDispatch.Router.Tests;

using Moq;
using NUnit.Framework;
using PwrDrvr.LambdaDispatch.Router;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using PwrDrvr.LambdaDispatch.Router.EmbeddedMetrics;

[TestFixture]
public class LambdaInstanceManagerTests
{
  [Test]
  public async Task AddConnectionForLambda_WhenLambdaDoesNotExist()
  {
    // Arrange
    var lambdaId = "testLambdaId";
    var channelId = "testChannelId";
    var dispatchMode = AddConnectionDispatchMode.Enqueue;

    var mockRequest = new Mock<HttpRequest>();
    var mockResponse = new Mock<HttpResponse>();
    var requestContext = new Mock<HttpContext>();
    var request = new Mock<HttpRequest>();
    request.Setup(i => i.HttpContext).Returns(requestContext.Object);
    var response = new Mock<HttpResponse>();
    var mockQueue = new Mock<ILeastOutstandingQueue>();
    var mockConfig = new Mock<IConfig>();
    var mockMetricsLogger = new Mock<IMetricsLogger>();

    var manager = new LambdaInstanceManager(mockQueue.Object, mockConfig.Object, mockMetricsLogger.Object);

    var expectedConnectionResult = new AddConnectionResult()
    {
      CanUseNow = false,
      Connection = null,
      WasRejected = true,
    };

    // Act
    var result = await manager.AddConnectionForLambda(mockRequest.Object, mockResponse.Object, lambdaId, channelId, dispatchMode);

    // Assert
    Assert.That(result, Is.EqualTo(expectedConnectionResult));
    mockQueue.Verify(q => q.ReinstateFullInstance(It.IsAny<ILambdaInstance>()), Times.Never);
  }

  [Test]
  public async Task AddConnectionForLambda_WhenLambdaExists_AddsConnectionAndReinstatesInstance()
  {
    // Arrange
    var lambdaId = "testLambdaId";
    var channelId = "testChannelId";
    var dispatchMode = AddConnectionDispatchMode.Enqueue;

    var mockRequest = new Mock<HttpRequest>();
    var mockResponse = new Mock<HttpResponse>();
    var requestContext = new Mock<HttpContext>();
    var request = new Mock<HttpRequest>();
    request.Setup(i => i.HttpContext).Returns(requestContext.Object);
    var response = new Mock<HttpResponse>();
    var mockInstance = new Mock<ILambdaInstance>();
    mockInstance.Setup(i => i.Id).Returns(lambdaId);
    mockInstance.Object.OnOpen += (instance) => { };
    var mockConnection = new Mock<LambdaConnection>(request.Object, response.Object, mockInstance.Object, "channel-1", false);
    var mockQueue = new Mock<ILeastOutstandingQueue>();
    var mockConfig = new Mock<IConfig>();
    var mockMetricsLogger = new Mock<IMetricsLogger>();

    var manager = new LambdaInstanceManager(mockQueue.Object, mockConfig.Object, mockMetricsLogger.Object);
    manager.TryAddInstance(mockInstance.Object);
    // Raise the OnOpen event to get the manager to think it has an instance
    mockInstance.Raise(m => m.OnOpen += null, mockInstance.Object);

    var expectedConnectionResult = new AddConnectionResult()
    {
      CanUseNow = false,
      Connection = mockConnection.Object,
      WasRejected = false,
    };
    mockInstance
        .Setup(i => i.AddConnection(mockRequest.Object, mockResponse.Object, channelId, dispatchMode))
          .ReturnsAsync(expectedConnectionResult);

    // Act
    var result = await manager.AddConnectionForLambda(mockRequest.Object, mockResponse.Object, lambdaId, channelId, dispatchMode);

    // Assert
    Assert.That(result, Is.EqualTo(expectedConnectionResult));
    mockInstance.Verify(i => i.AddConnection(mockRequest.Object, mockResponse.Object, channelId, dispatchMode), Times.Once);
    mockQueue.Verify(q => q.ReinstateFullInstance(mockInstance.Object), Times.Once);
  }

  [Test]
  public async Task AddConnectionForLambda_WhenLambdaExists_ConnectionRejectedDoesNotReinstate()
  {
    // Arrange
    var lambdaId = "testLambdaId";
    var channelId = "testChannelId";
    var dispatchMode = AddConnectionDispatchMode.Enqueue;

    var mockRequest = new Mock<HttpRequest>();
    var mockResponse = new Mock<HttpResponse>();
    var requestContext = new Mock<HttpContext>();
    var request = new Mock<HttpRequest>();
    request.Setup(i => i.HttpContext).Returns(requestContext.Object);
    var response = new Mock<HttpResponse>();
    var mockInstance = new Mock<ILambdaInstance>();
    mockInstance.Setup(i => i.Id).Returns(lambdaId);
    mockInstance.Object.OnOpen += (instance) => { };
    var mockConnection = new Mock<LambdaConnection>(request.Object, response.Object, mockInstance.Object, "channel-1", false);
    var mockQueue = new Mock<ILeastOutstandingQueue>();
    var mockConfig = new Mock<IConfig>();
    var mockMetricsLogger = new Mock<IMetricsLogger>();

    var manager = new LambdaInstanceManager(mockQueue.Object, mockConfig.Object, mockMetricsLogger.Object);
    manager.TryAddInstance(mockInstance.Object);
    // Raise the OnOpen event to get the manager to think it has an instance
    mockInstance.Raise(m => m.OnOpen += null, mockInstance.Object);

    var expectedConnectionResult = new AddConnectionResult()
    {
      CanUseNow = false,
      Connection = null,
      WasRejected = true,
    };
    mockInstance
        .Setup(i => i.AddConnection(mockRequest.Object, mockResponse.Object, channelId, dispatchMode))
          .ReturnsAsync(expectedConnectionResult);

    // Act
    var result = await manager.AddConnectionForLambda(mockRequest.Object, mockResponse.Object, lambdaId, channelId, dispatchMode);

    // Assert
    Assert.That(result, Is.EqualTo(expectedConnectionResult));
    mockInstance.Verify(i => i.AddConnection(mockRequest.Object, mockResponse.Object, channelId, dispatchMode), Times.Once);
    mockQueue.Verify(q => q.ReinstateFullInstance(mockInstance.Object), Times.Never);
  }

  [Test]
  public async Task AddConnectionForLambda_WhenLambdaExists_ImmediateDispatch()
  {
    // Arrange
    var lambdaId = "testLambdaId";
    var channelId = "testChannelId";
    var dispatchMode = AddConnectionDispatchMode.ImmediateDispatch;

    var mockRequest = new Mock<HttpRequest>();
    var mockResponse = new Mock<HttpResponse>();
    var requestContext = new Mock<HttpContext>();
    var request = new Mock<HttpRequest>();
    request.Setup(i => i.HttpContext).Returns(requestContext.Object);
    var response = new Mock<HttpResponse>();
    var mockInstance = new Mock<ILambdaInstance>();
    mockInstance.Setup(i => i.Id).Returns(lambdaId);
    mockInstance.Object.OnOpen += (instance) => { };
    var mockConnection = new Mock<LambdaConnection>(request.Object, response.Object, mockInstance.Object, "channel-1", false);
    var mockQueue = new Mock<ILeastOutstandingQueue>();
    var mockConfig = new Mock<IConfig>();
    var mockMetricsLogger = new Mock<IMetricsLogger>();

    var manager = new LambdaInstanceManager(mockQueue.Object, mockConfig.Object, mockMetricsLogger.Object);
    manager.TryAddInstance(mockInstance.Object);
    // Raise the OnOpen event to get the manager to think it has an instance
    mockInstance.Raise(m => m.OnOpen += null, mockInstance.Object);

    var expectedConnectionResult = new AddConnectionResult()
    {
      CanUseNow = false,
      Connection = mockConnection.Object,
      WasRejected = true,
    };
    mockInstance
        .Setup(i => i.AddConnection(mockRequest.Object, mockResponse.Object, channelId, dispatchMode))
          .ReturnsAsync(expectedConnectionResult);

    // Act
    var result = await manager.AddConnectionForLambda(mockRequest.Object, mockResponse.Object, lambdaId, channelId, dispatchMode);

    // Assert
    Assert.That(result, Is.EqualTo(expectedConnectionResult));
    mockInstance.Verify(i => i.AddConnection(mockRequest.Object, mockResponse.Object, channelId, dispatchMode), Times.Once);
    mockQueue.Verify(q => q.ReinstateFullInstance(mockInstance.Object), Times.Never);
  }

  [Test]
  public async Task AddConnectionForLambda_WhenLambdaExists_TentativeDispatch()
  {
    // Arrange
    var lambdaId = "testLambdaId";
    var channelId = "testChannelId";
    var dispatchMode = AddConnectionDispatchMode.TentativeDispatch;

    var mockRequest = new Mock<HttpRequest>();
    var mockResponse = new Mock<HttpResponse>();
    var requestContext = new Mock<HttpContext>();
    var request = new Mock<HttpRequest>();
    request.Setup(i => i.HttpContext).Returns(requestContext.Object);
    var response = new Mock<HttpResponse>();
    var mockInstance = new Mock<ILambdaInstance>();
    mockInstance.Setup(i => i.Id).Returns(lambdaId);
    mockInstance.Object.OnOpen += (instance) => { };
    var mockConnection = new Mock<LambdaConnection>(request.Object, response.Object, mockInstance.Object, "channel-1", false);
    var mockQueue = new Mock<ILeastOutstandingQueue>();
    var mockConfig = new Mock<IConfig>();
    var mockMetricsLogger = new Mock<IMetricsLogger>();

    var manager = new LambdaInstanceManager(mockQueue.Object, mockConfig.Object, mockMetricsLogger.Object);
    manager.TryAddInstance(mockInstance.Object);
    // Raise the OnOpen event to get the manager to think it has an instance
    mockInstance.Raise(m => m.OnOpen += null, mockInstance.Object);

    var expectedConnectionResult = new AddConnectionResult()
    {
      CanUseNow = true,
      Connection = mockConnection.Object,
      WasRejected = true,
    };
    mockInstance
        .Setup(i => i.AddConnection(mockRequest.Object, mockResponse.Object, channelId, dispatchMode))
          .ReturnsAsync(expectedConnectionResult);

    // Act
    var result = await manager.AddConnectionForLambda(mockRequest.Object, mockResponse.Object, lambdaId, channelId, dispatchMode);

    // Assert
    Assert.That(result, Is.EqualTo(expectedConnectionResult));
    mockInstance.Verify(i => i.AddConnection(mockRequest.Object, mockResponse.Object, channelId, dispatchMode), Times.Once);
    mockQueue.Verify(q => q.ReinstateFullInstance(mockInstance.Object), Times.Never);
  }
}