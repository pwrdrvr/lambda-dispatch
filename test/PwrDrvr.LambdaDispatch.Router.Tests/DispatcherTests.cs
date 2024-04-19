using Moq;
using Microsoft.Extensions.Logging;
using PwrDrvr.LambdaDispatch.Router.EmbeddedMetrics;
using Microsoft.AspNetCore.Http;
using Amazon.Lambda;
using App.Metrics.Counter;
using App.Metrics.Gauge;
using App.Metrics.Meter;
using PwrDrvr.LambdaDispatch.Router.Tests.Mocks;
using System.Diagnostics;

namespace PwrDrvr.LambdaDispatch.Router.Tests;

[TestFixture]
public class DispatcherTests
{
  private Mock<IAmazonLambda> _mocklambdaClient;
  private Mock<ILogger<Dispatcher>> _mockLogger;
  private Mock<IMetricsLogger> _mockMetricsLogger;
  private Mock<ILambdaInstanceManager> _mockLambdaInstanceManager;
  private Mock<IShutdownSignal> _mockShutdownSignal;
  private CancellationTokenSource _ctsShutdownSignal;
  private Mock<IMetricsRegistry> _metricsRegistry;

  [SetUp]
  public void SetUp()
  {
    _mocklambdaClient = new Mock<IAmazonLambda>();
    _mockLogger = new Mock<ILogger<Dispatcher>>();
    _mockMetricsLogger = new Mock<IMetricsLogger>();
    _mockShutdownSignal = new Mock<IShutdownSignal>();
    _ctsShutdownSignal = new CancellationTokenSource();
    _mockShutdownSignal = new Mock<IShutdownSignal>();
    _mockShutdownSignal.Setup(s => s.Shutdown).Returns(_ctsShutdownSignal);
    _mockLambdaInstanceManager = new Mock<ILambdaInstanceManager>();
    _metricsRegistry = MetricsRegistryMockFactory.Create();
  }

  [Test]
  public async Task AddConnectionForLambda_LambdaIdIsBlank_ReturnsLambdaIDNotFound()
  {
    // Arrange
    var mockRequest = new Mock<HttpRequest>();
    var mockResponse = new Mock<HttpResponse>();
    var mockConfig = new Mock<IConfig>();
    var dispatcher = new Dispatcher(_mockLogger.Object,
      _mockMetricsLogger.Object,
      _mockLambdaInstanceManager.Object,
      _mockShutdownSignal.Object,
      metricsRegistry: _metricsRegistry.Object,
      config: mockConfig.Object
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
    var mockConfig = new Mock<IConfig>();
    var dispatcher = new Dispatcher(_mockLogger.Object,
      _mockMetricsLogger.Object,
      _mockLambdaInstanceManager.Object,
      _mockShutdownSignal.Object,
      metricsRegistry: _metricsRegistry.Object,
      config: mockConfig.Object
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
    var mockConfig = new Mock<IConfig>();
    var dispatcher = new Dispatcher(_mockLogger.Object,
      _mockMetricsLogger.Object,
      _mockLambdaInstanceManager.Object,
      _mockShutdownSignal.Object,
      metricsRegistry: _metricsRegistry.Object,
      config: mockConfig.Object
      );
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
          _mockShutdownSignal.Object,
          metricsRegistry: _metricsRegistry.Object,
          config: mockConfig.Object
          );
    var instance = new LambdaInstance(maxConcurrentCount: maxConcurrentCount,
      functionName: "someFunc",
      poolId: "default",
      lambdaClient: _mocklambdaClient.Object,
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
      Assert.That(result.Connection?.State, Is.EqualTo(LambdaConnectionState.Open), "Connection.State");
    });

    _ctsShutdownSignal.Cancel();
  }

  [Test]
  public async Task AddIncomingRequest_ValidRequest_AfterLambdaConnection()
  {
    // Arrange
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
    var mockLambdaInstanceManager = new Mock<ILambdaInstanceManager>();
    var dispatcher = new Dispatcher(
          _mockLogger.Object,
          _mockMetricsLogger.Object,
          mockLambdaInstanceManager.Object,
          _mockShutdownSignal.Object,
          metricsRegistry: _metricsRegistry.Object,
          config: mockConfig.Object
          );
    var mockInstance = new Mock<ILambdaInstance>();
    var mockConnection = new Mock<LambdaConnection>(mockChannelRequest.Object, mockChannelResponse.Object, mockInstance.Object, "channel-1", false);

    ILambdaConnection? mockConnectionOut = mockConnection.Object;
    mockLambdaInstanceManager.Setup(l => l.TryGetConnection(out mockConnectionOut, false)).Returns(true);

    var mockIncomingContext = new Mock<HttpContext>();
    var mockIncomingRequest = new Mock<HttpRequest>();
    mockChannelRequest.Setup(i => i.HttpContext).Returns(mockIncomingContext.Object);
    var mockIncomingResponse = new Mock<HttpResponse>();

    // Act
    await dispatcher.AddRequest(mockIncomingRequest.Object, mockIncomingResponse.Object);

    // Assert
    _metricsRegistry.Verify(m => m.Metrics.Measure.Counter.Increment(It.IsAny<CounterOptions>()), Times.Exactly(3));
    _metricsRegistry.Verify(m => m.Metrics.Measure.Meter.Mark(It.IsAny<MeterOptions>(), 1), Times.Once);
    _metricsRegistry.Verify(m => m.Metrics.Measure.Gauge.SetValue(It.IsAny<GaugeOptions>(), It.IsAny<double>()), Times.Exactly(2));
    mockLambdaInstanceManager.Verify(l => l.TryGetConnection(out mockConnectionOut, false), Times.Once);

    _ctsShutdownSignal.Cancel();
  }

  [Test]
  public async Task AddIncomingRequest_ValidRequest_BeforeLambdaConnection()
  {
    // Arrange
    var mockChannelContext = new Mock<HttpContext>();
    var mockChannelRequest = new Mock<HttpRequest>();
    mockChannelRequest.Setup(i => i.HttpContext).Returns(mockChannelContext.Object);
    var mockChannelResponse = new Mock<HttpResponse>();
    var maxConcurrentCount = 1;
    var mockQueue = new Mock<ILambdaInstanceQueue>();
    var mockConfig = new Mock<IConfig>();
    mockConfig.SetupGet(c => c.MaxConcurrentCount).Returns(maxConcurrentCount);
    mockConfig.SetupGet(c => c.IncomingRequestTimeoutTimeSpan).Returns(TimeSpan.FromSeconds(10));
    var mockMetricsLogger = new Mock<IMetricsLogger>();
    var mockPoolOptions = new Mock<IPoolOptions>();
    var getCallbackIP = new Mock<IGetCallbackIP>();
    getCallbackIP.Setup(i => i.CallbackUrl).Returns("https://127.0.0.1:1000");
    var mockLambdaInstanceManager = new Mock<ILambdaInstanceManager>();
    var dispatcher = new Dispatcher(
          _mockLogger.Object,
          _mockMetricsLogger.Object,
          mockLambdaInstanceManager.Object,
          _mockShutdownSignal.Object,
          metricsRegistry: _metricsRegistry.Object,
          config: mockConfig.Object
          );
    var mockInstance = new Mock<ILambdaInstance>();

    ILambdaConnection? nullConnectionOut = null;

    // Return false to simulate no connection waiting to pickup the request
    mockLambdaInstanceManager.Setup(l => l.TryGetConnection(out nullConnectionOut, false)).Returns(false);

    // Return the mock lambda for the mock lambdaid
    var mockInstanceOut = mockInstance.Object;
    mockLambdaInstanceManager.Setup(m => m.ValidateLambdaId("lambdaId", out mockInstanceOut)).Returns(true);
    // mockLambdaInstanceManager.Setup(m => m.UpdateDesiredCapacity(1, 0, It.IsAny<double>(), It.IsAny<double>()));
    var mockIncomingContext = new Mock<HttpContext>();
    var mockIncomingRequest = new Mock<HttpRequest>();
    mockChannelRequest.Setup(i => i.HttpContext).Returns(mockIncomingContext.Object);
    var mockIncomingResponse = new Mock<HttpResponse>();

    var mockConnection = new Mock<LambdaConnection>(mockChannelRequest.Object, mockChannelResponse.Object, mockInstance.Object, "channel-1", false);
    ILambdaConnection? mockConnectionOut = mockConnection.Object;

    // Simulate success adding the connection for the lambda instance
    var addConnectionResult = new AddConnectionResult
    {
      Connection = mockConnection.Object,
      CanUseNow = true,
      WasRejected = false
    };
    mockLambdaInstanceManager.Setup(m => m.AddConnectionForLambda(mockChannelRequest.Object, mockChannelResponse.Object,
      "lambdaId", "channelId", AddConnectionDispatchMode.TentativeDispatch))
                  .Returns(Task.FromResult(addConnectionResult));

    // Schedule adding a connection in the background so it will release the request
    _ = Task.Run(async () =>
    {
      await Task.Delay(500);
      mockLambdaInstanceManager.Setup(l => l.TryGetConnection(out mockConnectionOut, false)).Returns(true);

      // Tell the dispatcher that we have a connection
      await dispatcher.AddConnectionForLambda(mockChannelRequest.Object, mockChannelResponse.Object, "lambdaId", "channelId");
    });

    // Act
    await dispatcher.AddRequest(mockIncomingRequest.Object, mockIncomingResponse.Object);

    // Assert
    Assert.Multiple(() =>
    {
      Assert.That(dispatcher.PendingRequestCount, Is.EqualTo(0));
      Assert.That(dispatcher.RunningRequestCount, Is.EqualTo(0));
    });
    mockIncomingResponse.Verify(r => r.CompleteAsync(), Times.AtLeast(2));
    // StartAsync does not actuall get called because we do not have a real LambdaInstance
    mockIncomingResponse.Verify(r => r.StartAsync(It.IsAny<CancellationToken>()), Times.Never);
    mockInstance.Verify(i => i.TryGetConnectionWillUse(mockConnection.Object), Times.Once);
    mockLambdaInstanceManager.Verify(m => m.UpdateDesiredCapacity(1, 0, It.IsAny<double>(), It.IsAny<double>()), Times.AtLeast(2));
    mockLambdaInstanceManager.Verify(m => m.UpdateDesiredCapacity(0, 1, It.IsAny<double>(), It.IsAny<double>()), Times.AtLeast(1));
    mockLambdaInstanceManager.Verify(m => m.UpdateDesiredCapacity(0, 0, It.IsAny<double>(), It.IsAny<double>()), Times.AtMost(5));
    mockLambdaInstanceManager.Verify(m => m.ValidateLambdaId("lambdaId", out mockInstanceOut), Times.Once);
    _metricsRegistry.Verify(m => m.Metrics.Measure.Counter.Increment(It.IsAny<CounterOptions>()), Times.Exactly(5));
    _metricsRegistry.Verify(m => m.Metrics.Measure.Meter.Mark(It.IsAny<MeterOptions>(), 1), Times.Once);
    // _metricsRegistry.Verify(m => m.Metrics.Measure.Gauge.SetValue(It.IsAny<GaugeOptions>(), It.IsAny<double>()), Times.Exactly(2));
    mockLambdaInstanceManager.Verify(l => l.TryGetConnection(out mockConnectionOut, false), Times.Once);
    mockLambdaInstanceManager.Verify(l => l.TryGetConnection(out nullConnectionOut, false), Times.Once);

    _ctsShutdownSignal.Cancel();
  }

  [Test]
  public async Task AddIncomingRequest_ValidRequest_WakeupBackgroundDispatcher()
  {
    // Arrange
    var mockChannelContext = new Mock<HttpContext>();
    var mockChannelRequest = new Mock<HttpRequest>();
    mockChannelRequest.Setup(i => i.HttpContext).Returns(mockChannelContext.Object);
    var mockChannelResponse = new Mock<HttpResponse>();
    var maxConcurrentCount = 1;
    var mockQueue = new Mock<ILambdaInstanceQueue>();
    var mockConfig = new Mock<IConfig>();
    mockConfig.SetupGet(c => c.MaxConcurrentCount).Returns(maxConcurrentCount);
    mockConfig.SetupGet(c => c.IncomingRequestTimeoutTimeSpan).Returns(TimeSpan.FromSeconds(10));
    var mockMetricsLogger = new Mock<IMetricsLogger>();
    var mockPoolOptions = new Mock<IPoolOptions>();
    var getCallbackIP = new Mock<IGetCallbackIP>();
    getCallbackIP.Setup(i => i.CallbackUrl).Returns("https://127.0.0.1:1000");
    var mockLambdaInstanceManager = new Mock<ILambdaInstanceManager>();
    var dispatcher = new Dispatcher(
          _mockLogger.Object,
          _mockMetricsLogger.Object,
          mockLambdaInstanceManager.Object,
          _mockShutdownSignal.Object,
          metricsRegistry: _metricsRegistry.Object,
          config: mockConfig.Object
          );
    var mockInstance = new Mock<ILambdaInstance>();
    mockInstance.Setup(i => i.Id).Returns("lambdaId");

    ILambdaConnection? nullConnectionOut = null;

    // Return false to simulate no connection waiting to pickup the request
    mockLambdaInstanceManager.Setup(l => l.TryGetConnection(out nullConnectionOut, false)).Returns(false);

    // Return the mock lambda for the mock lambdaid
    var mockInstanceOut = mockInstance.Object;
    mockLambdaInstanceManager.Setup(m => m.ValidateLambdaId("lambdaId", out mockInstanceOut)).Returns(true);
    var mockIncomingContext = new Mock<HttpContext>();
    var mockIncomingRequest = new Mock<HttpRequest>();
    mockChannelRequest.Setup(i => i.HttpContext).Returns(mockIncomingContext.Object);
    var mockIncomingResponse = new Mock<HttpResponse>();

    var mockConnection = new Mock<ILambdaConnection>();
    mockConnection.Setup(c => c.ChannelId).Returns("channelId");
    mockConnection.Setup(c => c.Instance).Returns(mockInstance.Object);
    mockConnection.Setup(c => c.State).Returns(LambdaConnectionState.Open);
    mockConnection.Setup(c => c.Request).Returns(mockChannelRequest.Object);
    mockConnection.Setup(c => c.Response).Returns(mockChannelResponse.Object);
    ILambdaConnection? mockConnectionOut = mockConnection.Object;

    // Simulate success adding the connection for the lambda instance
    var addConnectionResult = new AddConnectionResult
    {
      Connection = mockConnection.Object,
      CanUseNow = true,
      WasRejected = false
    };
    mockLambdaInstanceManager.Setup(m => m.AddConnectionForLambda(mockChannelRequest.Object, mockChannelResponse.Object,
      "lambdaId", "channelId", AddConnectionDispatchMode.TentativeDispatch))
                  .Returns(Task.FromResult(addConnectionResult));

    // Schedule adding a connection in the background so it will release the request
    _ = Task.Run(async () =>
    {
      await Task.Delay(500);

      // Tell the dispatcher that we have a connection
      dispatcher.WakeupBackgroundDispatcher(mockConnection.Object);
    });

    // Act
    await dispatcher.AddRequest(mockIncomingRequest.Object, mockIncomingResponse.Object);

    // Assert
    Assert.Multiple(() =>
    {
      Assert.That(dispatcher.PendingRequestCount, Is.EqualTo(0));
      Assert.That(dispatcher.RunningRequestCount, Is.EqualTo(0));
    });
    // Question - Why is CompleteAsync never called in this case?
    mockIncomingResponse.Verify(r => r.CompleteAsync(), Times.Never);
    // StartAsync does not actuall get called because we do not have a real LambdaInstance
    mockIncomingResponse.Verify(r => r.StartAsync(It.IsAny<CancellationToken>()), Times.Never);
    mockInstance.Verify(i => i.TryGetConnectionWillUse(mockConnection.Object), Times.Once);
    mockLambdaInstanceManager.Verify(m => m.UpdateDesiredCapacity(1, 0, It.IsAny<double>(), It.IsAny<double>()), Times.AtLeast(1));
    mockLambdaInstanceManager.Verify(m => m.UpdateDesiredCapacity(0, 1, It.IsAny<double>(), It.IsAny<double>()), Times.AtLeast(1));
    mockLambdaInstanceManager.Verify(m => m.UpdateDesiredCapacity(0, 0, It.IsAny<double>(), It.IsAny<double>()), Times.AtMost(5));
    mockLambdaInstanceManager.Verify(m => m.ValidateLambdaId("lambdaId", out mockInstanceOut), Times.Never);
    _metricsRegistry.Verify(m => m.Metrics.Measure.Counter.Increment(It.IsAny<CounterOptions>()), Times.Exactly(5));
    _metricsRegistry.Verify(m => m.Metrics.Measure.Meter.Mark(It.IsAny<MeterOptions>(), 1), Times.Once);
    // _metricsRegistry.Verify(m => m.Metrics.Measure.Gauge.SetValue(It.IsAny<GaugeOptions>(), It.IsAny<double>()), Times.Exactly(2));
    mockLambdaInstanceManager.Verify(l => l.TryGetConnection(out mockConnectionOut, false), Times.Once);
    mockLambdaInstanceManager.Verify(l => l.TryGetConnection(out nullConnectionOut, false), Times.Once);

    _ctsShutdownSignal.Cancel();
  }

  [Test]
  public async Task AddIncomingRequest_ValidRequest_ForegroundDispatch_TimeoutAfterDispatch_DecrementRunning()
  {
    // Arrange
    var mockChannelContext = new Mock<HttpContext>();
    var mockChannelRequest = new Mock<HttpRequest>();
    mockChannelRequest.Setup(i => i.HttpContext).Returns(mockChannelContext.Object);
    var mockChannelResponse = new Mock<HttpResponse>();
    var maxConcurrentCount = 1;
    var mockQueue = new Mock<ILambdaInstanceQueue>();
    var mockConfig = new Mock<IConfig>();
    mockConfig.SetupGet(c => c.MaxConcurrentCount).Returns(maxConcurrentCount);
    // This needs to be less than the delay below on RunRequest
    mockConfig.SetupGet(c => c.IncomingRequestTimeoutTimeSpan).Returns(TimeSpan.FromSeconds(1));
    var mockMetricsLogger = new Mock<IMetricsLogger>();
    var mockPoolOptions = new Mock<IPoolOptions>();
    var getCallbackIP = new Mock<IGetCallbackIP>();
    getCallbackIP.Setup(i => i.CallbackUrl).Returns("https://127.0.0.1:1000");
    var mockLambdaInstanceManager = new Mock<ILambdaInstanceManager>();
    var dispatcher = new Dispatcher(
          _mockLogger.Object,
          _mockMetricsLogger.Object,
          mockLambdaInstanceManager.Object,
          _mockShutdownSignal.Object,
          metricsRegistry: _metricsRegistry.Object,
          config: mockConfig.Object
          );
    var mockInstance = new Mock<ILambdaInstance>();
    var mockConnection = new Mock<ILambdaConnection>();

    ILambdaConnection? mockConnectionOut = mockConnection.Object;
    mockLambdaInstanceManager.Setup(l => l.TryGetConnection(out mockConnectionOut, false)).Returns(true);

    var mockIncomingContext = new Mock<HttpContext>();
    var mockIncomingRequest = new Mock<HttpRequest>();
    mockChannelRequest.Setup(i => i.HttpContext).Returns(mockIncomingContext.Object);
    var mockIncomingResponse = new Mock<HttpResponse>();

    // Mock the RunRequest function and make it sleep 2 seconds
    mockConnection.SetupGet(c => c.Request).Returns(mockChannelRequest.Object);
    mockConnection.SetupGet(c => c.Response).Returns(mockChannelResponse.Object);
    mockConnection.SetupGet(c => c.Instance).Returns(mockInstance.Object);
    mockConnection.SetupGet(c => c.ChannelId).Returns("channel-1");
    mockConnection.SetupGet(c => c.State).Returns(LambdaConnectionState.Open);
    mockConnection.Setup(c => c.RunRequest(mockIncomingRequest.Object, mockIncomingResponse.Object)).Returns(Task.Delay(5000));

    // Act
    var stopwatch = Stopwatch.StartNew();
    await dispatcher.AddRequest(mockIncomingRequest.Object, mockIncomingResponse.Object);
    stopwatch.Stop();

    // Assert
    Assert.Multiple(() =>
    {
      Assert.That(dispatcher.PendingRequestCount, Is.EqualTo(0));
      Assert.That(dispatcher.RunningRequestCount, Is.EqualTo(0));
      // TODO: We should split the concept of a request dispatch timeout
      // and a request idle timeout - so long as the request is active
      // within the idle timeout it should be left to run?
      // Assert.That(stopwatch.ElapsedMilliseconds, Is.LessThan(2000));
    });
    mockConnection.Verify(c => c.RunRequest(mockIncomingRequest.Object, mockIncomingResponse.Object), Times.Once);
    _metricsRegistry.Verify(m => m.Metrics.Measure.Counter.Increment(It.IsAny<CounterOptions>()), Times.AtLeast(1));
    _metricsRegistry.Verify(m => m.Metrics.Measure.Meter.Mark(It.IsAny<MeterOptions>(), 1), Times.AtLeast(1));
    _metricsRegistry.Verify(m => m.Metrics.Measure.Gauge.SetValue(It.IsAny<GaugeOptions>(), It.IsAny<double>()), Times.AtLeast(1));
    mockLambdaInstanceManager.Verify(l => l.TryGetConnection(out mockConnectionOut, false), Times.Once);

    _ctsShutdownSignal.Cancel();
  }

  [Test]
  public async Task AddIncomingRequest_ValidRequest_BackgroundDispatch_TimeoutBeforeDispatch_DecrementPending()
  {
    // Arrange
    var mockChannelContext = new Mock<HttpContext>();
    var mockChannelRequest = new Mock<HttpRequest>();
    mockChannelRequest.Setup(i => i.HttpContext).Returns(mockChannelContext.Object);
    var mockChannelResponse = new Mock<HttpResponse>();
    var maxConcurrentCount = 1;
    var mockQueue = new Mock<ILambdaInstanceQueue>();
    var mockConfig = new Mock<IConfig>();
    mockConfig.SetupGet(c => c.MaxConcurrentCount).Returns(maxConcurrentCount);
    mockConfig.SetupGet(c => c.IncomingRequestTimeoutTimeSpan).Returns(TimeSpan.FromSeconds(2));
    var mockMetricsLogger = new Mock<IMetricsLogger>();
    var mockPoolOptions = new Mock<IPoolOptions>();
    var getCallbackIP = new Mock<IGetCallbackIP>();
    getCallbackIP.Setup(i => i.CallbackUrl).Returns("https://127.0.0.1:1000");
    var mockLambdaInstanceManager = new Mock<ILambdaInstanceManager>();
    var dispatcher = new Dispatcher(
          _mockLogger.Object,
          _mockMetricsLogger.Object,
          mockLambdaInstanceManager.Object,
          _mockShutdownSignal.Object,
          metricsRegistry: _metricsRegistry.Object,
          config: mockConfig.Object
          );
    var mockInstance = new Mock<ILambdaInstance>();
    mockInstance.Setup(i => i.Id).Returns("lambdaId");

    ILambdaConnection? nullConnectionOut = null;

    // Return false to simulate no connection waiting to pickup the request
    mockLambdaInstanceManager.Setup(l => l.TryGetConnection(out nullConnectionOut, false)).Returns(false);

    // Return the mock lambda for the mock lambdaid
    var mockInstanceOut = mockInstance.Object;
    mockLambdaInstanceManager.Setup(m => m.ValidateLambdaId("lambdaId", out mockInstanceOut)).Returns(true);
    var mockIncomingContext = new Mock<HttpContext>();
    var mockIncomingRequest = new Mock<HttpRequest>();
    mockChannelRequest.Setup(i => i.HttpContext).Returns(mockIncomingContext.Object);
    var mockIncomingResponse = new Mock<HttpResponse>();

    // Act
    await dispatcher.AddRequest(mockIncomingRequest.Object, mockIncomingResponse.Object);

    // Assert
    Assert.Multiple(() =>
    {
      Assert.That(dispatcher.PendingRequestCount, Is.EqualTo(0));
      Assert.That(dispatcher.RunningRequestCount, Is.EqualTo(0));
    });
    mockIncomingResponse.Verify(r => r.CompleteAsync(), Times.Never);
    mockIncomingResponse.Verify(r => r.StartAsync(It.IsAny<CancellationToken>()), Times.Never);
    mockLambdaInstanceManager.Verify(m => m.UpdateDesiredCapacity(1, 0, It.IsAny<double>(), It.IsAny<double>()), Times.AtLeast(2));
    mockLambdaInstanceManager.Verify(m => m.UpdateDesiredCapacity(0, 1, It.IsAny<double>(), It.IsAny<double>()), Times.Never);
    mockLambdaInstanceManager.Verify(m => m.UpdateDesiredCapacity(0, 0, It.IsAny<double>(), It.IsAny<double>()), Times.AtMost(5));
    mockLambdaInstanceManager.Verify(m => m.ValidateLambdaId("lambdaId", out mockInstanceOut), Times.Never);
    _metricsRegistry.Verify(m => m.Metrics.Measure.Counter.Increment(It.IsAny<CounterOptions>()), Times.AtMost(5));
    _metricsRegistry.Verify(m => m.Metrics.Measure.Meter.Mark(It.IsAny<MeterOptions>(), 1), Times.Once);
    // _metricsRegistry.Verify(m => m.Metrics.Measure.Gauge.SetValue(It.IsAny<GaugeOptions>(), It.IsAny<double>()), Times.Exactly(2));
    mockLambdaInstanceManager.Verify(l => l.TryGetConnection(out nullConnectionOut, false), Times.Once);

    _ctsShutdownSignal.Cancel();
  }

  [Test]
  public async Task AddIncomingRequest_ValidRequest_BackgroundDispatch_TimeoutAfterDispatch()
  {
    // Arrange
    var mockChannelContext = new Mock<HttpContext>();
    var mockChannelRequest = new Mock<HttpRequest>();
    mockChannelRequest.Setup(i => i.HttpContext).Returns(mockChannelContext.Object);
    var mockChannelResponse = new Mock<HttpResponse>();
    var maxConcurrentCount = 1;
    var mockQueue = new Mock<ILambdaInstanceQueue>();
    var mockConfig = new Mock<IConfig>();
    mockConfig.SetupGet(c => c.MaxConcurrentCount).Returns(maxConcurrentCount);
    mockConfig.SetupGet(c => c.IncomingRequestTimeoutTimeSpan).Returns(TimeSpan.FromSeconds(2));
    var mockMetricsLogger = new Mock<IMetricsLogger>();
    var mockPoolOptions = new Mock<IPoolOptions>();
    var getCallbackIP = new Mock<IGetCallbackIP>();
    getCallbackIP.Setup(i => i.CallbackUrl).Returns("https://127.0.0.1:1000");
    var mockLambdaInstanceManager = new Mock<ILambdaInstanceManager>();
    var dispatcher = new Dispatcher(
          _mockLogger.Object,
          _mockMetricsLogger.Object,
          mockLambdaInstanceManager.Object,
          _mockShutdownSignal.Object,
          metricsRegistry: _metricsRegistry.Object,
          config: mockConfig.Object
          );
    var mockInstance = new Mock<ILambdaInstance>();
    mockInstance.Setup(i => i.Id).Returns("lambdaId");

    var mockConnection = new Mock<ILambdaConnection>();

    ILambdaConnection? mockConnectionOut = mockConnection.Object;
    mockLambdaInstanceManager.Setup(l => l.TryGetConnection(out mockConnectionOut, false)).Returns(true);

    // Return the mock lambda for the mock lambdaid
    var mockInstanceOut = mockInstance.Object;
    mockLambdaInstanceManager.Setup(m => m.ValidateLambdaId("lambdaId", out mockInstanceOut)).Returns(true);
    var mockIncomingContext = new Mock<HttpContext>();
    var mockIncomingRequest = new Mock<HttpRequest>();
    mockChannelRequest.Setup(i => i.HttpContext).Returns(mockIncomingContext.Object);
    var mockIncomingResponse = new Mock<HttpResponse>();

    ILambdaConnection? nullConnectionOut = null;

    // Return false to simulate no connection waiting to pickup the request
    mockLambdaInstanceManager.Setup(l => l.TryGetConnection(out nullConnectionOut, false)).Returns(false);

    // Schedule adding a connection in the background so it will release the request
    _ = Task.Run(async () =>
    {
      await Task.Delay(500);

      // Mock the RunRequest function and make it sleep 2 seconds
      mockConnection.SetupGet(c => c.Request).Returns(mockChannelRequest.Object);
      mockConnection.SetupGet(c => c.Response).Returns(mockChannelResponse.Object);
      mockConnection.SetupGet(c => c.Instance).Returns(mockInstance.Object);
      mockConnection.SetupGet(c => c.ChannelId).Returns("channel-1");
      mockConnection.SetupGet(c => c.State).Returns(LambdaConnectionState.Open);
      mockConnection.Setup(c => c.RunRequest(mockIncomingRequest.Object, mockIncomingResponse.Object)).Returns(Task.Delay(5000));

      // Allow the dispatcher to return our connection
      mockLambdaInstanceManager.Setup(l => l.TryGetConnection(out mockConnectionOut, false)).Returns(true);
    });

    // Act
    var stopwatch = Stopwatch.StartNew();
    await dispatcher.AddRequest(mockIncomingRequest.Object, mockIncomingResponse.Object);
    stopwatch.Stop();

    // Assert
    Assert.Multiple(() =>
    {
      Assert.That(stopwatch.ElapsedMilliseconds, Is.LessThan(3000));
      Assert.That(dispatcher.PendingRequestCount, Is.EqualTo(0));
      Assert.That(dispatcher.RunningRequestCount, Is.EqualTo(0));
    });
    mockIncomingResponse.Verify(r => r.CompleteAsync(), Times.Never);
    mockIncomingResponse.Verify(r => r.StartAsync(It.IsAny<CancellationToken>()), Times.Never);
    mockLambdaInstanceManager.Verify(m => m.UpdateDesiredCapacity(1, 0, It.IsAny<double>(), It.IsAny<double>()), Times.AtLeast(2));
    mockLambdaInstanceManager.Verify(m => m.UpdateDesiredCapacity(0, 1, It.IsAny<double>(), It.IsAny<double>()), Times.Never);
    mockLambdaInstanceManager.Verify(m => m.UpdateDesiredCapacity(0, 0, It.IsAny<double>(), It.IsAny<double>()), Times.AtMost(5));
    mockLambdaInstanceManager.Verify(m => m.ValidateLambdaId("lambdaId", out mockInstanceOut), Times.Never);
    _metricsRegistry.Verify(m => m.Metrics.Measure.Counter.Increment(It.IsAny<CounterOptions>()), Times.AtMost(5));
    _metricsRegistry.Verify(m => m.Metrics.Measure.Meter.Mark(It.IsAny<MeterOptions>(), 1), Times.Once);
    // _metricsRegistry.Verify(m => m.Metrics.Measure.Gauge.SetValue(It.IsAny<GaugeOptions>(), It.IsAny<double>()), Times.Exactly(2));
    mockLambdaInstanceManager.Verify(l => l.TryGetConnection(out nullConnectionOut, false), Times.Once);

    _ctsShutdownSignal.Cancel();
  }
}
