using Moq;

namespace PwrDrvr.LambdaDispatch.Router.Tests;

[TestFixture]
public class RoundRobinLambdaInstanceQueueTests
{
  private RoundRobinLambdaInstanceQueue _queue;
  private Mock<ILambdaInstance> _mockInstance;

  [SetUp]
  public void SetUp()
  {
    _queue = new RoundRobinLambdaInstanceQueue();
    _mockInstance = new Mock<ILambdaInstance>();
  }

  [Test]
  public void AddInstance_ShouldAddInstanceToQueue()
  {
    _queue.AddInstance(_mockInstance.Object);

    Assert.That(_queue.TryRemoveInstance(out ILambdaInstance? instance), Is.True);
    Assert.That(instance, Is.EqualTo(_mockInstance.Object));
  }

  [Test]
  public void TryGetConnection_ShouldReturnFalseIfNoInstances()
  {
    Assert.That(_queue.TryGetConnection(out LambdaConnection? connection), Is.False);
  }

  [Test]
  public void TryGetConnection_ShouldReturnTrueIfInstanceHasConnection()
  {
    _mockInstance.Setup(i => i.TryGetConnection(out It.Ref<LambdaConnection?>.IsAny, It.IsAny<bool>())).Returns(true);
    _queue.AddInstance(_mockInstance.Object);

    Assert.That(_queue.TryGetConnection(out LambdaConnection? connection), Is.True);
  }

  [Test]
  public void TryGetConnection_ShouldReturnFalseIfInstanceHasNoConnection()
  {
    _mockInstance.Setup(i => i.TryGetConnection(out It.Ref<LambdaConnection?>.IsAny, It.IsAny<bool>())).Returns(false);
    _queue.AddInstance(_mockInstance.Object);

    Assert.That(_queue.TryGetConnection(out LambdaConnection? connection), Is.False);
  }

  [Test]
  public void TryGetConnection_ShouldCallTryGetConnectionOnEachInstanceAndReturnConnectionFromLastInstance()
  {
    Mock<ILambdaInstance> _mockInstanceWithoutConnection;
    Mock<ILambdaInstance> _mockInstanceWithConnection;

    _queue = new RoundRobinLambdaInstanceQueue();
    _mockInstanceWithoutConnection = new Mock<ILambdaInstance>();
    _mockInstanceWithConnection = new Mock<ILambdaInstance>();

    _mockInstanceWithoutConnection.Setup(i => i.TryGetConnection(out It.Ref<LambdaConnection?>.IsAny, It.IsAny<bool>())).Returns(false);
    _mockInstanceWithConnection.Setup(i => i.TryGetConnection(out It.Ref<LambdaConnection?>.IsAny, It.IsAny<bool>())).Returns(true);

    for (int i = 0; i < 9; i++)
    {
      _queue.AddInstance(_mockInstanceWithoutConnection.Object);
    }

    _queue.AddInstance(_mockInstanceWithConnection.Object);

    Assert.That(_queue.TryGetConnection(out LambdaConnection? connection), Is.True);

    _mockInstanceWithoutConnection.Verify(i => i.TryGetConnection(out It.Ref<LambdaConnection?>.IsAny, It.IsAny<bool>()), Times.Exactly(9));
    _mockInstanceWithConnection.Verify(i => i.TryGetConnection(out It.Ref<LambdaConnection?>.IsAny, It.IsAny<bool>()), Times.Once);
  }

  [Test]
  public void TryGetConnection_ShouldReturnConnectionFromCorrectInstance()
  {
    var mockInstance1 = new Mock<ILambdaInstance>();
    var mockInstance2 = new Mock<ILambdaInstance>();
    var mockInstance3 = new Mock<ILambdaInstance>();

    var callCount = 0;
    mockInstance1.Setup(i => i.TryGetConnection(out It.Ref<LambdaConnection?>.IsAny, It.IsAny<bool>()))
        .Returns(() => callCount++ == 0 ? false : true);
    mockInstance2.Setup(i => i.TryGetConnection(out It.Ref<LambdaConnection?>.IsAny, It.IsAny<bool>())).Returns(false);
    mockInstance3.Setup(i => i.TryGetConnection(out It.Ref<LambdaConnection?>.IsAny, It.IsAny<bool>())).Returns(true);

    _queue.AddInstance(mockInstance1.Object);
    _queue.AddInstance(mockInstance2.Object);
    _queue.AddInstance(mockInstance3.Object);

    Assert.That(_queue.TryGetConnection(out LambdaConnection? connection), Is.True);
    mockInstance1.Verify(i => i.TryGetConnection(out It.Ref<LambdaConnection?>.IsAny, It.IsAny<bool>()), Times.Once);
    mockInstance2.Verify(i => i.TryGetConnection(out It.Ref<LambdaConnection?>.IsAny, It.IsAny<bool>()), Times.Once);
    // This gives the connection on 1st call
    mockInstance3.Verify(i => i.TryGetConnection(out It.Ref<LambdaConnection?>.IsAny, It.IsAny<bool>()), Times.Once);

    Assert.That(_queue.TryGetConnection(out connection), Is.True);
    // This gives the connection on the 2nd call
    mockInstance1.Verify(i => i.TryGetConnection(out It.Ref<LambdaConnection?>.IsAny, It.IsAny<bool>()), Times.Exactly(2));
    mockInstance2.Verify(i => i.TryGetConnection(out It.Ref<LambdaConnection?>.IsAny, It.IsAny<bool>()), Times.Once);
    mockInstance3.Verify(i => i.TryGetConnection(out It.Ref<LambdaConnection?>.IsAny, It.IsAny<bool>()), Times.Once);

    Assert.That(_queue.TryGetConnection(out connection), Is.True);
    mockInstance1.Verify(i => i.TryGetConnection(out It.Ref<LambdaConnection?>.IsAny, It.IsAny<bool>()), Times.Exactly(2));
    mockInstance2.Verify(i => i.TryGetConnection(out It.Ref<LambdaConnection?>.IsAny, It.IsAny<bool>()), Times.Exactly(2));
    // This gives the connection on the 3rd call
    mockInstance3.Verify(i => i.TryGetConnection(out It.Ref<LambdaConnection?>.IsAny, It.IsAny<bool>()), Times.Exactly(2));

    Assert.That(_queue.TryGetConnection(out connection), Is.True);
    // This gives the connection on the 4th call
    mockInstance1.Verify(i => i.TryGetConnection(out It.Ref<LambdaConnection?>.IsAny, It.IsAny<bool>()), Times.Exactly(3));
    mockInstance2.Verify(i => i.TryGetConnection(out It.Ref<LambdaConnection?>.IsAny, It.IsAny<bool>()), Times.Exactly(2));
    mockInstance3.Verify(i => i.TryGetConnection(out It.Ref<LambdaConnection?>.IsAny, It.IsAny<bool>()), Times.Exactly(2));

    Assert.That(_queue.TryGetConnection(out connection), Is.True);
    mockInstance1.Verify(i => i.TryGetConnection(out It.Ref<LambdaConnection?>.IsAny, It.IsAny<bool>()), Times.Exactly(3));
    mockInstance2.Verify(i => i.TryGetConnection(out It.Ref<LambdaConnection?>.IsAny, It.IsAny<bool>()), Times.Exactly(3));
    // This gives the connection on the 5th call
    mockInstance3.Verify(i => i.TryGetConnection(out It.Ref<LambdaConnection?>.IsAny, It.IsAny<bool>()), Times.Exactly(3));
  }
}