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
  [TestCase(10, 2, 100, 2, ExpectedResult = 7)]
  [TestCase(20, 4, 1000, 110, ExpectedResult = 57)]
  [TestCase(10, 1, 10000, 0, ExpectedResult = 600)]
  public int SimpleDesiredInstanceCount_ReturnsExpectedResult(
    int maxConcurrentCount, int instanceCountMultiplier, int pendingRequests, int runningRequests)
  {
    var capacityManager = new CapacityManager(maxConcurrentCount, instanceCountMultiplier);
    return capacityManager.SimpleDesiredInstanceCount(pendingRequests, runningRequests);
  }

  [TestCase(0, 0, 0, 0, ExpectedResult = 0)]
  [TestCase(10, 2, 0, 0, ExpectedResult = 0)]
  [TestCase(10, 2, 1, 0, ExpectedResult = 1)]
  [TestCase(10, 2, 1, 1, ExpectedResult = 1)]
  [TestCase(10, 2, 1, 1500, ExpectedResult = 1)]
  [TestCase(10, 2, 1, 59500, ExpectedResult = 15)]
  [TestCase(10, 2, 3000, 21, ExpectedResult = 16)]
  [TestCase(10, 2, 3000, 6, ExpectedResult = 5)]
  [TestCase(1, 1, 1, 1, ExpectedResult = 1)]
  [TestCase(10, 5, 2, 2, ExpectedResult = 1)]
  [TestCase(20, 10, 5, 2, ExpectedResult = 1)]
  [TestCase(10, 2, 100, 2, ExpectedResult = 1)]
  [TestCase(20, 4, 1000, 100, ExpectedResult = 24)]
  public int EwmaDesiredInstanceCount_ReturnsExpectedResult(
   int maxConcurrentCount, int instanceCountMultiplier, double requestsPerSecondEWMA, double requestDurationEWMA)
  {
    var capacityManager = new CapacityManager(maxConcurrentCount, instanceCountMultiplier);
    return capacityManager.EwmaDesiredInstanceCount(requestsPerSecondEWMA, requestDurationEWMA, 0);
  }

  [TestCase(10, 2, 1, 0, 2, ExpectedResult = 2)]
  [TestCase(10, 2, 1, 1, 2, ExpectedResult = 2)]
  [TestCase(10, 2, 1, 1500, 2, ExpectedResult = 2)]
  [TestCase(10, 2, 1, 59500, 16, ExpectedResult = 16)]
  [TestCase(10, 2, 3000, 21, 17, ExpectedResult = 17)]
  [TestCase(10, 2, 3000, 6, 6, ExpectedResult = 6)]
  [TestCase(1, 1, 1, 1, 2, ExpectedResult = 2)]
  [TestCase(10, 5, 2, 2, 2, ExpectedResult = 2)]
  [TestCase(20, 10, 5, 2, 2, ExpectedResult = 2)]
  [TestCase(10, 2, 100, 2, 2, ExpectedResult = 2)]
  [TestCase(20, 4, 1000, 100, 25, ExpectedResult = 25)]
  public int EwmaDesiredInstanceCount_SkipsScaleOnOneLess(
  int maxConcurrentCount, int instanceCountMultiplier, double requestsPerSecondEWMA, double requestDurationEWMA, int currentDesiredCount)
  {
    var capacityManager = new CapacityManager(maxConcurrentCount, instanceCountMultiplier);
    return capacityManager.EwmaDesiredInstanceCount(requestsPerSecondEWMA, requestDurationEWMA, currentDesiredCount);
  }

  // Scale Out Tests
  [TestCase(0, 0, 0, 0.33, 0.33, ExpectedResult = 0)]
  [TestCase(10, 2, 0, 0.33, 0.33, ExpectedResult = 3)]
  [TestCase(25, 15, 5, 0.5, 0.33, ExpectedResult = 23)]
  [TestCase(10, 2, 5, 0.33, 0.33, ExpectedResult = 7)]
  [TestCase(10, 2, 1, 0.33, 0.33, ExpectedResult = 3)]
  [TestCase(1, 1, 1, 0.33, 0.33, ExpectedResult = 1)]
  [TestCase(13, 3, 2, 1, 0.33, ExpectedResult = 6)]
  [TestCase(13, 3, 5, 1, 0.33, ExpectedResult = 8)]
  [TestCase(20, 10, 5, 0.33, 0.33, ExpectedResult = 15)]
  [TestCase(10, 2, 100, 0.33, 0.33, ExpectedResult = 10)]
  // Scale In Tests
  [TestCase(2, 10, 0, 0.33, 0.33, ExpectedResult = 6)]
  [TestCase(0, 1, 5, 0.33, 0.33, ExpectedResult = 0)]
  [TestCase(13, 30, 5, 0.33, 0.33, ExpectedResult = 20)]
  [TestCase(0, 400, 5, 0.33, 0.33, ExpectedResult = 0)]
  [TestCase(0, 4, 5, 0.1, 0.1, ExpectedResult = 0)]
  public int ConstrainDesiredInstanceCount_ReturnsExpectedResult(
    int proposedDesiredInstanceCount, int currentDesiredInstanceCount,
      int maxScaleOut, double maxScaleOutRatio, double maxScaleInRatio)
  {
    var capacityManager = new CapacityManager(10, 2);
    return capacityManager.ConstrainDesiredInstanceCount(proposedDesiredInstanceCount, currentDesiredInstanceCount,
      maxScaleOut, maxScaleOutRatio, maxScaleInRatio);
  }
}