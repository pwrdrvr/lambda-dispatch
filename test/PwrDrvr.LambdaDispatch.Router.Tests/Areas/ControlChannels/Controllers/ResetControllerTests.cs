using Moq;
using PwrDrvr.LambdaDispatch.Router.ControlChannels.Controllers;
using PwrDrvr.LambdaDispatch.Router.Tests.Mocks;

namespace PwrDrvr.LambdaDispatch.Router.ControlChannels.Tests;

public class ResetControllerTests
{
  private ResetController _controller;
  private Mock<IMetricsRegistry> _mockMetricsRegistry;

  [SetUp]
  public void SetUp()
  {
    _mockMetricsRegistry = MetricsRegistryMockFactory.Create();
    _controller = new ResetController(_mockMetricsRegistry.Object);
  }

  [Test]
  public void HandleRequest_CallsResetOnMetricsRegistry()
  {
    _controller.HandleRequest();

    _mockMetricsRegistry.Verify(m => m.Reset(), Times.Once);
  }
}