using Moq;
using Microsoft.Extensions.Logging;
using PwrDrvr.LambdaDispatch.Router.EmbeddedMetrics;
using Microsoft.AspNetCore.Http;
using Amazon.Lambda;

namespace PwrDrvr.LambdaDispatch.Router.Tests;


[TestFixture]
public class DispatcherTests
{
  private Mock<ILogger<Dispatcher>> _mockLogger;
  private Mock<IMetricsLogger> _mockMetricsLogger;
  private Mock<ILambdaInstanceManager> _mockLambdaInstanceManager;
  private Mock<IShutdownSignal> _mockShutdownSignal;

  [SetUp]
  public void SetUp()
  {
    _mockLogger = new Mock<ILogger<Dispatcher>>();
    _mockMetricsLogger = new Mock<IMetricsLogger>();
    _mockShutdownSignal = new Mock<IShutdownSignal>();
    _mockLambdaInstanceManager = new Mock<ILambdaInstanceManager>();

  }

  [Test]
  public async Task AddConnectionForLambda_LambdaIdIsBlank_ReturnsLambdaIDNotFound()
  {
    // Arrange
    var mockRequest = new Mock<HttpRequest>();
    var mockResponse = new Mock<HttpResponse>();
    var dispatcher = new Dispatcher(_mockLogger.Object,
      _mockMetricsLogger.Object,
      _mockLambdaInstanceManager.Object,
      _mockShutdownSignal.Object
    );

    // Act
    var result = await dispatcher.AddConnectionForLambda(mockRequest.Object, mockResponse.Object, "", "channelId");

    // Assert
    Assert.IsTrue(result.LambdaIDNotFound);
  }

  [Test]
  public async Task AddConnectionForLambda_InvalidLambdaId_ReturnsLambdaIDNotFound()
  {
    // Arrange
    var mockRequest = new Mock<HttpRequest>();
    var mockResponse = new Mock<HttpResponse>();
    var dispatcher = new Dispatcher(_mockLogger.Object,
      _mockMetricsLogger.Object,
      _mockLambdaInstanceManager.Object,
      _mockShutdownSignal.Object
    );
    _mockLambdaInstanceManager.Setup(m => m.ValidateLambdaId(It.IsAny<string>(), out It.Ref<ILambdaInstance>.IsAny)).Returns(false);

    // Act
    var result = await dispatcher.AddConnectionForLambda(mockRequest.Object, mockResponse.Object, "lambdaId", "channelId");

    // Assert
    Assert.IsTrue(result.LambdaIDNotFound);
  }

  [Test]
  public async Task AddConnectionForLambda_ValidLambdaId_AddsConnection()
  {
    // Arrange
    var requestContext = new Mock<HttpContext>();
    var mockRequest = new Mock<HttpRequest>();
    mockRequest.Setup(i => i.HttpContext).Returns(requestContext.Object);
    var mockResponse = new Mock<HttpResponse>();
    var mockInstance = new Mock<ILambdaInstance>();
    var lambdaId = "lambdaId";
    mockInstance.Setup(i => i.Id).Returns(lambdaId);
    mockInstance.Object.OnOpen += (instance) => { };
    var dispatcher = new Dispatcher(_mockLogger.Object,
      _mockMetricsLogger.Object,
      _mockLambdaInstanceManager.Object,
      _mockShutdownSignal.Object
    );
    var mockConnection = new Mock<LambdaConnection>(mockRequest.Object, mockResponse.Object, mockInstance.Object, "channel-1", false);
    _mockLambdaInstanceManager.Setup(m => m.ValidateLambdaId(
        It.IsAny<string>(),
        out It.Ref<ILambdaInstance>.IsAny))
      .Returns(true);
    _mockLambdaInstanceManager.Setup(m => m.AddConnectionForLambda(
        It.IsAny<HttpRequest>(),
        It.IsAny<HttpResponse>(),
        It.IsAny<string>(),
        It.IsAny<string>(),
        It.IsAny<AddConnectionDispatchMode>()))
      .ReturnsAsync(new AddConnectionResult { CanUseNow = true, Connection = mockConnection.Object });
    // TODO: Add ReenqueueConnectionForLambda mock so we can make sure it is not called

    // Act
    var result = await dispatcher.AddConnectionForLambda(mockRequest.Object, mockResponse.Object, "lambdaId", "channelId");

    // Assert
    Assert.Multiple(() =>
    {
      Assert.That(result.LambdaIDNotFound, Is.False);
      Assert.That(mockConnection.Object, Is.EqualTo(result.Connection));
    });
  }

  [Test]
  public async Task AddConnectionForLambda_NoDoubleEnqueue()
  {
    // Arrange
    var shutdownSignal = new ShutdownSignal();
    var lambdaClient = new Mock<IAmazonLambda>();
    var requestContext = new Mock<HttpContext>();
    var mockRequest = new Mock<HttpRequest>();
    mockRequest.Setup(i => i.HttpContext).Returns(requestContext.Object);
    var mockResponse = new Mock<HttpResponse>();
    var maxConcurrentCount = 1;
    var channelId = "channelId";
    var mockQueue = new Mock<ILeastOutstandingQueue>();
    var mockConfig = new Mock<IConfig>();
    mockConfig.SetupGet(c => c.MaxConcurrentCount).Returns(maxConcurrentCount);
    var mockMetricsLogger = new Mock<IMetricsLogger>();
    var manager = new LambdaInstanceManager(mockQueue.Object, mockConfig.Object, mockMetricsLogger.Object);
    var dispatcher = new Dispatcher(_mockLogger.Object,
          _mockMetricsLogger.Object,
          manager,
          shutdownSignal
        );
    var instance = new LambdaInstance(maxConcurrentCount, "somefunc", null, lambdaClient.Object, dispatcher);
    GetCallbackIP.Init(1000, "https", "127.0.0.1");

    Assert.Multiple(() =>
    {
      Assert.That(instance.IsOpen, Is.False);
      Assert.That(instance.State, Is.EqualTo(LambdaInstanceState.Initial));
    });
    manager.TryAddInstance(instance);
    instance.Start();
    Assert.Multiple(() =>
    {
      Assert.That(instance.IsOpen, Is.False);
      Assert.That(instance.State, Is.EqualTo(LambdaInstanceState.Starting));
    });

    // Act
    var result = await dispatcher.AddConnectionForLambda(mockRequest.Object, mockResponse.Object, instance.Id, channelId);

    // Assert
    Assert.Multiple(() =>
    {
      Assert.That(instance.IsOpen, Is.True, "IsOpen");
      Assert.That(instance.State, Is.EqualTo(LambdaInstanceState.Open));
      // The connection is not added to the queue so we can try to dispatch to it
      Assert.That(instance.QueueApproximateCount, Is.EqualTo(0), "QueueApproximateCount");
      Assert.That(result.LambdaIDNotFound, Is.False, "LambdaIDNotFound");
      Assert.That(result.ImmediatelyDispatched, Is.False, "ImmediatelyDispatched");
      Assert.That(result.Connection, Is.Not.Null, "Connection");
      Assert.That(result.Connection.State, Is.EqualTo(LambdaConnectionState.Open), "Connection.State");
    });

    // We have to wait for the background dispatcher to pick up the connection
    await Task.Delay(100);
    Assert.That(instance.QueueApproximateCount, Is.EqualTo(1), "QueueApproximateCount");

    // Act
    result = await dispatcher.AddConnectionForLambda(mockRequest.Object, mockResponse.Object, instance.Id, channelId);

    // Assert
    Assert.Multiple(() =>
    {
      Assert.That(instance.IsOpen, Is.True, "IsOpen");
      Assert.That(instance.State, Is.EqualTo(LambdaInstanceState.Open));
      // The connection will be added to the queue because we're already at max concurrent
      Assert.That(instance.QueueApproximateCount, Is.EqualTo(2), "QueueApproximateCount");
      Assert.That(result.LambdaIDNotFound, Is.False, "LambdaIDNotFound");
      Assert.That(result.ImmediatelyDispatched, Is.False, "ImmediatelyDispatched");
      Assert.That(result.Connection, Is.Not.Null, "Connection");
      Assert.That(result.Connection.State, Is.EqualTo(LambdaConnectionState.Open), "Connection.State");
    });

    await Task.Delay(100);
    // It is important that this count be `2` and not `3` as `3` indicates a double-add bug
    Assert.That(instance.QueueApproximateCount, Is.EqualTo(2), "QueueApproximateCount");

    shutdownSignal.Shutdown.Cancel();
  }
}