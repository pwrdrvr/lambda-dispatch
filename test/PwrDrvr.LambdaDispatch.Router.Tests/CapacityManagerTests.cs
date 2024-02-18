namespace PwrDrvr.LambdaDispatch.Router.Tests;

using NUnit.Framework;

[TestFixture]
public class CapacityManagerTests
{
  [TestCase(0, 0, 0, 0, ExpectedResult = 0)]
  [TestCase(10, 2, 0, 0, ExpectedResult = 0)]
  [TestCase(10, 2, 1, 1, ExpectedResult = 1)]
  [TestCase(1, 1, 1, 1, ExpectedResult = 2)]
  [TestCase(10, 5, 2, 2, ExpectedResult = 2)]
  [TestCase(20, 10, 5, 2, ExpectedResult = 2)]
  [TestCase(10, 2, 100, 2, ExpectedResult = 6)]
  // 47 would give 235 target connections but 940 total connections
  [TestCase(20, 4, 1000, 110, ExpectedResult = 47)]
  public int SimpleDesiredInstanceCount_ReturnsExpectedResult(
    int maxConcurrentCount, int instanceCountMultiplier, int pendingRequests, int runningRequests)
  {
    var capacityManager = new CapacityManager(maxConcurrentCount, instanceCountMultiplier);
    return capacityManager.SimpleDesiredInstanceCount(pendingRequests, runningRequests);
  }
}