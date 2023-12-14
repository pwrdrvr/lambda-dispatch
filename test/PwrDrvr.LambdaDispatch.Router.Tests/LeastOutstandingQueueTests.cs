using NUnit.Framework;
using PwrDrvr.LambdaDispatch.Router;
using System.Linq;

namespace PwrDrvr.LambdaDispatch.Tests
{
  public class LeastOutstandingQueueTests
  {
    private readonly int _maxConcurrentCount = 10;

    [SetUp]
    public void Setup()
    {
    }

    [Test]
    public void AddInstance_ShouldAddInstanceToQueue()
    {
      var queue = new LeastOutstandingQueue(_maxConcurrentCount);

      var instance = new LambdaInstance(_maxConcurrentCount);

      queue.AddInstance(instance);

      var result = queue.TryGetLeastOustandingConnection(out var connection);

      Assert.IsFalse(result);
      Assert.IsNull(connection);
    }

    [Test]
    public void TryGetLeastOutstandingConnection_ShouldReturnConnection()
    {
      var queue = new LeastOutstandingQueue(_maxConcurrentCount);
      var instance = new LambdaInstance(_maxConcurrentCount);

      var connection = instance.AddConnection(null, null, "");

      queue.AddInstance(instance);


      Assert.IsNotNull(connection);

      var result = queue.TryGetLeastOustandingConnection(out var dequeuedConnection);

      Assert.IsTrue(result);
      Assert.IsNotNull(dequeuedConnection);
    }
  }
}