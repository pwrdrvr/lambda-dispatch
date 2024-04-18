using Moq;
using Microsoft.Extensions.Logging;
using PwrDrvr.LambdaDispatch.Router.EmbeddedMetrics;
using Microsoft.AspNetCore.Http;
using Amazon.Lambda;
using App.Metrics.Counter;
using App.Metrics.Gauge;
using App.Metrics.Meter;
using PwrDrvr.LambdaDispatch.Router.Tests.Mocks;

namespace PwrDrvr.LambdaDispatch.Router.Tests;

[TestFixture]
public class DispatcherTests
{
  private Mock<ILogger<Dispatcher>> _mockLogger;
  private Mock<IMetricsLogger> _mockMetricsLogger;
  private Mock<ILambdaInstanceManager> _mockLambdaInstanceManager;
  private Mock<IShutdownSignal> _mockShutdownSignal;
  private Mock<IMetricsRegistry> _metricsRegistry;

  [SetUp]
  public void SetUp()
  {
    _mockLogger = new Mock<ILogger<Dispatcher>>();
    _mockMetricsLogger = new Mock<IMetricsLogger>();
    _mockShutdownSignal = new Mock<IShutdownSignal>();
    _mockLambdaInstanceManager = new Mock<ILambdaInstanceManager>();
    _metricsRegistry = MetricsRegistryMockFactory.Create();
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
      _mockShutdownSignal.Object,
      metricsRegistry: _metricsRegistry.Object
    );

    // Act
    var result = await dispatcher.AddConnectionForLambda(mockRequest.Object, mockResponse.Object, "", "channelId");

    // Assert
    Assert.That(result.LambdaIDNotFound, Is.True);
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
      _mockShutdownSignal.Object,
      metricsRegistry: _metricsRegistry.Object
    );
    _mockLambdaInstanceManager.Setup(m => m.ValidateLambdaId(It.IsAny<string>(), out It.Ref<ILambdaInstance?>.IsAny)).Returns(false);

    // Act
    var result = await dispatcher.AddConnectionForLambda(mockRequest.Object, mockResponse.Object, "lambdaId", "channelId");

    // Assert
    Assert.That(result.LambdaIDNotFound, Is.True);
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
      _mockShutdownSignal.Object,
      metricsRegistry: _metricsRegistry.Object);
    var mockConnection = new Mock<LambdaConnection>(mockRequest.Object, mockResponse.Object, mockInstance.Object, "channel-1", false);
    _mockLambdaInstanceManager.Setup(m => m.ValidateLambdaId(
        It.IsAny<string>(),
        out It.Ref<ILambdaInstance?>.IsAny))
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
    var mockQueue = new Mock<ILambdaInstanceQueue>();
    var mockConfig = new Mock<IConfig>();
    mockConfig.SetupGet(c => c.MaxConcurrentCount).Returns(maxConcurrentCount);
    var mockMetricsLogger = new Mock<IMetricsLogger>();
    var mockPoolOptions = new Mock<IPoolOptions>();
    var getCallbackIP = new Mock<IGetCallbackIP>();
    getCallbackIP.Setup(i => i.CallbackUrl).Returns("https://127.0.0.1:1000");
    var mockLambdaClientConfig = new Mock<ILambdaClientConfig>();
    var manager = new LambdaInstanceManager(mockQueue.Object,
      mockConfig.Object,
      mockMetricsLogger.Object,
      mockPoolOptions.Object,
      getCallbackIP.Object,
      _metricsRegistry.Object,
      lambdaClientConfig: mockLambdaClientConfig.Object);
    var dispatcher = new Dispatcher(_mockLogger.Object,
          _mockMetricsLogger.Object,
          manager,
          shutdownSignal,
          metricsRegistry: _metricsRegistry.Object);
    var instance = new LambdaInstance(maxConcurrentCount: maxConcurrentCount,
      functionName: "someFunc",
      poolId: "default",
      lambdaClient: lambdaClient.Object,
      dispatcher: dispatcher,
      getCallbackIP: getCallbackIP.Object,
      metricsRegistry: _metricsRegistry.Object,
      lambdaClientConfig: mockLambdaClientConfig.Object
      );

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
    await Task.Delay(100);

    // Assert
    Assert.Multiple(() =>
    {
      Assert.That(instance.IsOpen, Is.True, "IsOpen");
      Assert.That(instance.State, Is.EqualTo(LambdaInstanceState.Open));
      // We have to wait for the background dispatcher to pick up the connection
      // The connection is not added to the queue so we can try to dispatch to it
      Assert.That(instance.QueueApproximateCount, Is.EqualTo(1), "QueueApproximateCount");
      Assert.That(result.LambdaIDNotFound, Is.False, "LambdaIDNotFound");
      Assert.That(result.ImmediatelyDispatched, Is.False, "ImmediatelyDispatched");
      Assert.That(result.Connection, Is.Not.Null, "Connection");
      Assert.That(result.Connection.State, Is.EqualTo(LambdaConnectionState.Open), "Connection.State");
    });

    // Act
    result = await dispatcher.AddConnectionForLambda(mockRequest.Object, mockResponse.Object, instance.Id, channelId);
    await Task.Delay(100);

    // Assert
    Assert.Multiple(() =>
    {
      Assert.That(instance.IsOpen, Is.True, "IsOpen");
      Assert.That(instance.State, Is.EqualTo(LambdaInstanceState.Open));
      // The connection will be added to the queue because we're already at max concurrent
      // It is important that this count be `2` and not `3` as `3` indicates a double-add bug
      Assert.That(instance.QueueApproximateCount, Is.EqualTo(2), "QueueApproximateCount");
      Assert.That(result.LambdaIDNotFound, Is.False, "LambdaIDNotFound");
      Assert.That(result.ImmediatelyDispatched, Is.False, "ImmediatelyDispatched");
      Assert.That(result.Connection, Is.Not.Null, "Connection");
      Assert.That(result.Connection.State, Is.EqualTo(LambdaConnectionState.Open), "Connection.State");
    });

    shutdownSignal.Shutdown.Cancel();
  }

  [Test]
  public async Task AddRequest_ValidRequest_IncrementsMetricsAndDispatches()
  {
    // Arrange
    var shutdownSignal = new Mock<IShutdownSignal>();
    var lambdaClient = new Mock<IAmazonLambda>();
    var mockChannelContext = new Mock<HttpContext>();
    var mockChannelRequest = new Mock<HttpRequest>();
    mockChannelRequest.Setup(i => i.HttpContext).Returns(mockChannelContext.Object);
    var mockChannelResponse = new Mock<HttpResponse>();
    var maxConcurrentCount = 1;
    var mockQueue = new Mock<ILambdaInstanceQueue>();
    var mockConfig = new Mock<IConfig>();
    mockConfig.SetupGet(c => c.MaxConcurrentCount).Returns(maxConcurrentCount);
    var mockMetricsLogger = new Mock<IMetricsLogger>();
    var mockPoolOptions = new Mock<IPoolOptions>();
    var getCallbackIP = new Mock<IGetCallbackIP>();
    getCallbackIP.Setup(i => i.CallbackUrl).Returns("https://127.0.0.1:1000");
    var mockLambdaClientConfig = new Mock<ILambdaClientConfig>();
    var mockLambdaInstanceManager = new Mock<ILambdaInstanceManager>();
    var dispatcher = new Dispatcher(
          _mockLogger.Object,
          _mockMetricsLogger.Object,
          mockLambdaInstanceManager.Object,
          shutdownSignal.Object,
          metricsRegistry: _metricsRegistry.Object);
    var mockInstance = new Mock<ILambdaInstance>();
    var mockConnection = new Mock<LambdaConnection>(mockChannelRequest.Object, mockChannelResponse.Object, mockInstance.Object, "channel-1", false);

    ILambdaConnection? mockConnectionOut = mockConnection.Object;
    mockLambdaInstanceManager.Setup(l => l.TryGetConnection(out mockConnectionOut, false)).Returns(true);

    var mockIncomingContenxt = new Mock<HttpContext>();
    var mockIncomingRequest = new Mock<HttpRequest>();
    mockChannelRequest.Setup(i => i.HttpContext).Returns(mockIncomingContenxt.Object);
    var mockIncomingResponse = new Mock<HttpResponse>();

    // Act
    await dispatcher.AddRequest(mockIncomingRequest.Object, mockIncomingResponse.Object);

    // Assert
    _metricsRegistry.Verify(m => m.Metrics.Measure.Counter.Increment(It.IsAny<CounterOptions>()), Times.Exactly(3));
    _metricsRegistry.Verify(m => m.Metrics.Measure.Meter.Mark(It.IsAny<MeterOptions>(), 1), Times.Once);
    _metricsRegistry.Verify(m => m.Metrics.Measure.Gauge.SetValue(It.IsAny<GaugeOptions>(), It.IsAny<double>()), Times.Exactly(2));
    mockLambdaInstanceManager.Verify(l => l.TryGetConnection(out mockConnectionOut, false), Times.Once);
  }
}
