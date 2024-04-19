using NUnit.Framework;
using Moq;
using Microsoft.Extensions.Logging;
using PwrDrvr.LambdaDispatch.Router.EmbeddedMetrics;
using PwrDrvr.LambdaDispatch.Router.Tests.Mocks;

namespace PwrDrvr.LambdaDispatch.Router.Tests;

[TestFixture]
public class DispatcherSimpleTests
{
  private Dispatcher _dispatcher;
  private Mock<ILogger<Dispatcher>> _mockLogger;
  private Mock<IMetricsLogger> _mockMetricsLogger;
  private Mock<ILambdaInstanceManager> _mockLambdaInstanceManager;
  private Mock<IShutdownSignal> _mockShutdownSignal;
  private Mock<IMetricsRegistry> _metricsRegistry;
  private Mock<IConfig> _mockConfig;

  [SetUp]
  public void SetUp()
  {
    _mockLambdaInstanceManager = new Mock<ILambdaInstanceManager>();
    _mockLogger = new Mock<ILogger<Dispatcher>>();
    _mockMetricsLogger = new Mock<IMetricsLogger>();
    _mockShutdownSignal = new Mock<IShutdownSignal>();
    _metricsRegistry = MetricsRegistryMockFactory.Create();
    _mockConfig = new Mock<IConfig>();
    _mockConfig.SetupGet(c => c.IncomingRequestTimeoutTimeSpan).Returns(TimeSpan.FromSeconds(10));
    _dispatcher = new Dispatcher(_mockLogger.Object,
          _mockMetricsLogger.Object,
          _mockLambdaInstanceManager.Object,
          _mockShutdownSignal.Object,
          metricsRegistry: _metricsRegistry.Object,
          config: _mockConfig.Object
          );
  }

  [Test]
  public void PingInstance_ValidInstanceId_ReturnsTrue()
  {
    string instanceId = "valid-instance-id";
    _mockLambdaInstanceManager.Setup(l => l.ValidateLambdaId(instanceId, out It.Ref<ILambdaInstance?>.IsAny)).Returns(true);

    bool result = _dispatcher.PingInstance(instanceId);

    Assert.That(result, Is.True);
  }

  [Test]
  public void PingInstance_InvalidInstanceId_ReturnsFalse()
  {
    string instanceId = "invalid-instance-id";
    _mockLambdaInstanceManager.Setup(l => l.ValidateLambdaId(instanceId, out It.Ref<ILambdaInstance?>.IsAny)).Returns(false);

    bool result = _dispatcher.PingInstance(instanceId);

    Assert.That(result, Is.False);
  }

  [Test]
  public async Task CloseInstance_ValidInstanceId_CallsCloseInstance()
  {
    string instanceId = "valid-instance-id";
    var lambdaInstance = new Mock<ILambdaInstance>();
    var lambdaInstanceOut = lambdaInstance.Object;
    _mockLambdaInstanceManager.Setup(l => l.ValidateLambdaId(instanceId, out lambdaInstanceOut)).Returns(true);

    await _dispatcher.CloseInstance(instanceId);

    _mockLambdaInstanceManager.Verify(l => l.CloseInstance(lambdaInstanceOut, false), Times.Once);
  }

  [Test]
  public async Task CloseInstance_InvalidInstanceId_DoesNotCallCloseInstance()
  {
    string instanceId = "invalid-instance-id";
    _mockLambdaInstanceManager.Setup(l => l.ValidateLambdaId(instanceId, out It.Ref<ILambdaInstance?>.IsAny)).Returns(false);

    await _dispatcher.CloseInstance(instanceId);

    _mockLambdaInstanceManager.Verify(l => l.CloseInstance(It.IsAny<LambdaInstance>(), It.IsAny<bool>()), Times.Never);
  }
}