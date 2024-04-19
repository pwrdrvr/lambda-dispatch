using Moq;

namespace PwrDrvr.LambdaDispatch.Router.Tests;

[TestFixture]
public class RoundRobinLambdaInstanceQueue2Tests
{
  private RoundRobinLambdaInstanceQueue2 _queue;
  private Mock<ILambdaInstance> _mockInstance;

  [SetUp]
  public void SetUp()
  {
    _queue = new RoundRobinLambdaInstanceQueue2();
    _mockInstance = new Mock<ILambdaInstance>();
  }

  [Test]
  public void AddInstance_ShouldAddInstanceToQueue()
  {
    _mockInstance.SetupGet(i => i.IsOpen).Returns(true);
    _queue.AddInstance(_mockInstance.Object);
  }

  [Test]
  public void TryRemoveInstance_ShouldCleanQueue()
  {
    Mock<ILambdaInstance> _mockClosedInstance = new Mock<ILambdaInstance>();
    Mock<ILambdaInstance> _mockOpenInstance = new Mock<ILambdaInstance>();

    _mockClosedInstance.SetupGet(i => i.IsOpen).Returns(false);
    _mockOpenInstance.SetupGet(i => i.IsOpen).Returns(true);
    _mockOpenInstance.Setup(i => i.TryGetConnection(out It.Ref<ILambdaConnection?>.IsAny, It.IsAny<bool>())).Returns(true);
    _queue.AddInstance(_mockClosedInstance.Object);
    _queue.AddInstance(_mockClosedInstance.Object);
    _queue.AddInstance(_mockClosedInstance.Object);
    _queue.AddInstance(_mockOpenInstance.Object);
    _queue.AddInstance(_mockClosedInstance.Object);

    Assert.That(_queue.TryGetConnection(out ILambdaConnection? connection), Is.True);
    Assert.That(_queue.TryGetConnection(out connection), Is.True);
    _mockOpenInstance.Verify(i => i.TryGetConnection(out It.Ref<ILambdaConnection?>.IsAny, It.IsAny<bool>()), Times.Exactly(2));
    _mockOpenInstance.Verify(i => i.IsOpen, Times.Exactly(2));
    _mockClosedInstance.Verify(i => i.TryGetConnection(out It.Ref<ILambdaConnection?>.IsAny, It.IsAny<bool>()), Times.Never);
    _mockClosedInstance.Verify(i => i.IsOpen, Times.Exactly(7));

    Assert.That(_queue.TryRemoveInstance(out ILambdaInstance? instance), Is.True);
    Assert.That(instance, Is.EqualTo(_mockOpenInstance.Object));
    _mockOpenInstance.Verify(i => i.IsOpen, Times.Exactly(3));
    _mockClosedInstance.Verify(i => i.IsOpen, Times.Exactly(11));

    Assert.That(_queue.TryGetConnection(out connection), Is.False);
    _mockOpenInstance.Verify(i => i.TryGetConnection(out It.Ref<ILambdaConnection?>.IsAny, It.IsAny<bool>()), Times.Exactly(2));

    // Nothing left to remove
    Assert.That(_queue.TryRemoveInstance(out instance), Is.False);

    // Open the closed instance
    // Show that we still get no connection
    _mockClosedInstance.SetupGet(i => i.IsOpen).Returns(true);
    _queue.AddInstance(_mockInstance.Object);
    Assert.That(_queue.TryGetConnection(out connection), Is.False);
    _mockClosedInstance.Verify(i => i.TryGetConnection(out It.Ref<ILambdaConnection?>.IsAny, It.IsAny<bool>()), Times.Never);
    Assert.That(_queue.TryRemoveInstance(out instance), Is.False);
  }

  [Test]
  public void TryGetConnection_ShouldReturnFalseIfNoInstances()
  {
    Assert.That(_queue.TryGetConnection(out ILambdaConnection? connection), Is.False);
  }

  [Test]
  public void TryGetConnection_ShouldReturnTrueIfInstanceHasConnection()
  {
    _mockInstance.Setup(i => i.IsOpen).Returns(true);
    _mockInstance.Setup(i => i.TryGetConnection(out It.Ref<ILambdaConnection?>.IsAny, It.IsAny<bool>())).Returns(true);
    _queue.AddInstance(_mockInstance.Object);

    Assert.That(_queue.TryGetConnection(out ILambdaConnection? connection), Is.True);
  }

  [Test]
  public void TryGetConnection_ShouldReturnFalseIfInstanceHasNoConnection()
  {
    _mockInstance.Setup(i => i.TryGetConnection(out It.Ref<ILambdaConnection?>.IsAny, It.IsAny<bool>())).Returns(false);
    _queue.AddInstance(_mockInstance.Object);

    Assert.That(_queue.TryGetConnection(out ILambdaConnection? connection), Is.False);
  }

  [Test]
  public void TryGetConnection_ShouldCallTryGetConnectionOnEachInstanceAndReturnConnectionFromLastInstance()
  {
    Mock<ILambdaInstance> _mockInstanceWithoutConnection;
    Mock<ILambdaInstance> _mockInstanceWithConnection;

    _queue = new RoundRobinLambdaInstanceQueue2();
    _mockInstanceWithoutConnection = new Mock<ILambdaInstance>();
    _mockInstanceWithConnection = new Mock<ILambdaInstance>();

    _mockInstanceWithoutConnection.Setup(i => i.IsOpen).Returns(true);
    _mockInstanceWithoutConnection.Setup(i => i.TryGetConnection(out It.Ref<ILambdaConnection?>.IsAny, It.IsAny<bool>())).Returns(false);
    _mockInstanceWithConnection.Setup(i => i.IsOpen).Returns(true);
    _mockInstanceWithConnection.Setup(i => i.TryGetConnection(out It.Ref<ILambdaConnection?>.IsAny, It.IsAny<bool>())).Returns(true);

    for (int i = 0; i < 9; i++)
    {
      _queue.AddInstance(_mockInstanceWithoutConnection.Object);
    }

    _queue.AddInstance(_mockInstanceWithConnection.Object);

    Assert.That(_queue.TryGetConnection(out ILambdaConnection? connection), Is.True);

    _mockInstanceWithoutConnection.Verify(i => i.TryGetConnection(out It.Ref<ILambdaConnection?>.IsAny, It.IsAny<bool>()), Times.Exactly(9));
    _mockInstanceWithConnection.Verify(i => i.TryGetConnection(out It.Ref<ILambdaConnection?>.IsAny, It.IsAny<bool>()), Times.Once);
  }

  [Test]
  public void TryGetConnection_ShouldReturnConnectionFromCorrectInstance()
  {
    var mockInstance1 = new Mock<ILambdaInstance>();
    var mockInstance2 = new Mock<ILambdaInstance>();
    var mockInstance3 = new Mock<ILambdaInstance>();
    var mockInstance4 = new Mock<ILambdaInstance>();

    var callCount = 0;
    mockInstance1.Setup(i => i.IsOpen).Returns(true);
    mockInstance1.Setup(i => i.TryGetConnection(out It.Ref<ILambdaConnection?>.IsAny, It.IsAny<bool>()))
        .Returns(() => callCount++ == 0 ? false : true);
    mockInstance2.Setup(i => i.IsOpen).Returns(true);
    mockInstance2.Setup(i => i.TryGetConnection(out It.Ref<ILambdaConnection?>.IsAny, It.IsAny<bool>())).Returns(false);
    mockInstance3.Setup(i => i.IsOpen).Returns(true);
    mockInstance3.Setup(i => i.TryGetConnection(out It.Ref<ILambdaConnection?>.IsAny, It.IsAny<bool>())).Returns(true);
    mockInstance4.Setup(i => i.IsOpen).Returns(false);
    mockInstance4.Setup(i => i.TryGetConnection(out It.Ref<ILambdaConnection?>.IsAny, It.IsAny<bool>())).Returns(true);

    _queue.AddInstance(mockInstance1.Object);
    _queue.AddInstance(mockInstance2.Object);
    _queue.AddInstance(mockInstance3.Object);
    _queue.AddInstance(mockInstance4.Object);

    Assert.That(_queue.TryGetConnection(out ILambdaConnection? connection), Is.True);
    mockInstance1.Verify(i => i.TryGetConnection(out It.Ref<ILambdaConnection?>.IsAny, It.IsAny<bool>()), Times.Once);
    mockInstance2.Verify(i => i.TryGetConnection(out It.Ref<ILambdaConnection?>.IsAny, It.IsAny<bool>()), Times.Once);
    // This gives the connection on 1st call
    mockInstance3.Verify(i => i.TryGetConnection(out It.Ref<ILambdaConnection?>.IsAny, It.IsAny<bool>()), Times.Once);

    Assert.That(_queue.TryGetConnection(out connection), Is.True);
    // This gives the connection on the 2nd call
    mockInstance1.Verify(i => i.TryGetConnection(out It.Ref<ILambdaConnection?>.IsAny, It.IsAny<bool>()), Times.Exactly(2));
    mockInstance2.Verify(i => i.TryGetConnection(out It.Ref<ILambdaConnection?>.IsAny, It.IsAny<bool>()), Times.Once);
    mockInstance3.Verify(i => i.TryGetConnection(out It.Ref<ILambdaConnection?>.IsAny, It.IsAny<bool>()), Times.Once);

    Assert.That(_queue.TryGetConnection(out connection), Is.True);
    mockInstance1.Verify(i => i.TryGetConnection(out It.Ref<ILambdaConnection?>.IsAny, It.IsAny<bool>()), Times.Exactly(2));
    mockInstance2.Verify(i => i.TryGetConnection(out It.Ref<ILambdaConnection?>.IsAny, It.IsAny<bool>()), Times.Exactly(2));
    // This gives the connection on the 3rd call
    mockInstance3.Verify(i => i.TryGetConnection(out It.Ref<ILambdaConnection?>.IsAny, It.IsAny<bool>()), Times.Exactly(2));

    Assert.That(_queue.TryGetConnection(out connection), Is.True);
    // This gives the connection on the 4th call
    mockInstance1.Verify(i => i.TryGetConnection(out It.Ref<ILambdaConnection?>.IsAny, It.IsAny<bool>()), Times.Exactly(3));
    mockInstance2.Verify(i => i.TryGetConnection(out It.Ref<ILambdaConnection?>.IsAny, It.IsAny<bool>()), Times.Exactly(2));
    mockInstance3.Verify(i => i.TryGetConnection(out It.Ref<ILambdaConnection?>.IsAny, It.IsAny<bool>()), Times.Exactly(2));

    Assert.That(_queue.TryGetConnection(out connection), Is.True);
    mockInstance1.Verify(i => i.TryGetConnection(out It.Ref<ILambdaConnection?>.IsAny, It.IsAny<bool>()), Times.Exactly(3));
    mockInstance2.Verify(i => i.TryGetConnection(out It.Ref<ILambdaConnection?>.IsAny, It.IsAny<bool>()), Times.Exactly(3));
    // This gives the connection on the 5th call
    mockInstance3.Verify(i => i.TryGetConnection(out It.Ref<ILambdaConnection?>.IsAny, It.IsAny<bool>()), Times.Exactly(3));

    mockInstance4.Verify(i => i.IsOpen, Times.Exactly(2));
    mockInstance4.Verify(i => i.TryGetConnection(out It.Ref<ILambdaConnection?>.IsAny, It.IsAny<bool>()), Times.Never);
  }
}