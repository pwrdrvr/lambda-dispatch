using PwrDrvr.LambdaDispatch.Router;

[TestFixture]
public class WeightedAverageTests
{
  [Test]
  public void TestAdd()
  {
    var weightedAverage = new WeightedAverage(60);
    weightedAverage.Add(5);
    Thread.Sleep(1100); // Wait for more than one second to ensure the count is processed
    Assert.Greater(weightedAverage.EWMA, 0);
  }

  [Test]
  public void TestEWMA()
  {
    var weightedAverage = new WeightedAverage(60);
    weightedAverage.Add(5);
    Thread.Sleep(200);
    var ewma0 = weightedAverage.EWMA;

    Thread.Sleep(1100); // Wait for more than one second to ensure the count is processed
    var ewma1 = weightedAverage.EWMA;

    weightedAverage.Add(10);
    Thread.Sleep(1100); // Wait for more than one second to ensure the count is processed
    var ewma2 = weightedAverage.EWMA;

    Assert.Greater(ewma2, ewma1);
  }

  [Test]
  public void TestCleanupOldData()
  {
    var weightedAverage = new WeightedAverage(2);
    weightedAverage.Add(5);
    Thread.Sleep(1100); // Wait for more than one second to ensure the count is processed
    weightedAverage.Add(10);
    Thread.Sleep(2100); // Wait for more than two seconds to ensure the old data is cleaned up
    var ewma = weightedAverage.EWMA;

    Assert.LessOrEqual(ewma, 10);
  }
}