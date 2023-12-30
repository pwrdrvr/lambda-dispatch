using Moq;
using NUnit.Framework;
using PwrDrvr.LambdaDispatch.Router;

namespace PwrDrvr.LambdaDispatch.Tests
{
  public class LeastOutstandingQueueTests
  {
    [SetUp]
    public void Setup()
    {
    }

    [Test]
    public void AddInstance_ShouldAddInstanceToQueue()
    {
      var maxConcurrentCount = 10;
      var queue = new LeastOutstandingQueue(maxConcurrentCount);

      var instance = new LambdaInstance(maxConcurrentCount);

      queue.AddInstance(instance);

      var result = queue.TryGetLeastOustandingConnection(out var connection);

      Assert.IsFalse(result);
      Assert.IsNull(connection);
    }

    [Test]
    public void TryGetLeastOutstandingConnection_ShouldReturnConnection()
    {
      var maxConcurrentCount = 10;
      var queue = new LeastOutstandingQueue(maxConcurrentCount);

      var requestContext = new Mock<Microsoft.AspNetCore.Http.HttpContext>();
      var request = new Mock<Microsoft.AspNetCore.Http.HttpRequest>();
      request.Setup(i => i.HttpContext).Returns(requestContext.Object);
      var response = new Mock<Microsoft.AspNetCore.Http.HttpResponse>();

      var instance = new Mock<ILambdaInstance>();
      var connection = new Mock<LambdaConnection>(request.Object, response.Object, instance.Object, "channel-1");

      var id = "instance-1";
      instance.Setup(i => i.Id).Returns(id);
      instance.Setup(i => i.State).Returns(LambdaInstanceState.Open);
      instance.Setup(i => i.AvailableConnectionCount).Returns(1);
      var connectionObject = connection.Object;
      instance.Setup(i => i.TryGetConnection(out connectionObject)).Returns(true);

      // Add the instance
      queue.AddInstance(instance.Object);

      var result = queue.TryGetLeastOustandingConnection(out var dequeuedConnection);

      Assert.IsTrue(result);
      Assert.IsNotNull(dequeuedConnection);
    }
  }
}