namespace PwrDrvr.LambdaDispatch.Router.Tests;

using PwrDrvr.LambdaDispatch.Router;

[TestFixture]
public class WeightedAverageTests
{
  [Test]
  [Retry(3)]
  public void TestAdd()
  {
    var weightedAverage = new WeightedAverage(60);
    weightedAverage.Add(5);
    Thread.Sleep(150);
    Assert.That(weightedAverage.EWMA, Is.GreaterThan(0));
  }

  [Test]
  [Retry(3)]
  public void TestEWMA()
  {
    var weightedAverage = new WeightedAverage(5);
    weightedAverage.Add(50);
    Thread.Sleep(150);
    var ewma0 = weightedAverage.EWMA;
    Assert.That(ewma0, Is.GreaterThan(3));

    weightedAverage.Add(50);
    Thread.Sleep(150);
    var ewma1 = weightedAverage.EWMA;
    Assert.That(ewma1, Is.GreaterThan(3));

    weightedAverage.Add(200);
    Thread.Sleep(150);
    var ewma2 = weightedAverage.EWMA;

    Assert.That(ewma2, Is.GreaterThan(ewma1));
  }

  // [Test]
  // public void TestCleanupOldData()
  // {
  //   var weightedAverage = new WeightedAverage(2);
  //   weightedAverage.Add(5);
  //   Thread.Sleep(1100);
  //   weightedAverage.Add(10);
  //   Thread.Sleep(2100); // Wait for more than two seconds to ensure the old data is cleaned up
  //   var ewma = weightedAverage.EWMA;

  //   Assert.LessOrEqual(ewma, 10);
  // }

  [Test]
  public void TestMeanTrue()
  {
    // Arrange
    var weightedAverage = new WeightedAverage(5, true);

    // Act
    for (int i = 1; i <= 10; i++)
    {
      weightedAverage.Add(i);
    }

    Thread.Sleep(150); // Sleep to allow the background task to compute the EWMA

    // Assert
    // The exact value of EWMA will depend on the timing of the test, so we can't check for a specific value.
    // Instead, we can check that it's within a reasonable range.
    Assert.That(weightedAverage.EWMA, Is.GreaterThan(0));
    Assert.That(weightedAverage.EWMA, Is.LessThan(10));
  }

  [Test]
  [Retry(3)]
  public async Task TestConstantRateCalc()
  {
    // Arrange
    var weightedAverage = new WeightedAverage(5);
    var timer = new System.Timers.Timer(50);
    var counter = 0;

    timer.Elapsed += (sender, e) =>
    {
      for (int i = 0; i < 200; i++)
      {
        weightedAverage.Add(1);
      }

      counter++;

      // Stop the timer after approximately 5 seconds
      if (counter >= 100)
      {
        timer.Stop();
      }
    };

    // Act
    timer.Start();

    double lastEWMA = 0;

    await Task.Delay(110);

    lastEWMA = weightedAverage.EWMA;

    // Wait for the timer to finish
    while (timer.Enabled)
    {
      await Task.Delay(110);

      double currentEWMA = weightedAverage.EWMA;

      Assert.That(currentEWMA, Is.InRange(lastEWMA * .95, lastEWMA * 4));

      // The exact value of EWMA will depend on the timing of the test, so we can't check for a specific value.
      // Instead, we can check that it's within a reasonable range.
      Assert.That(weightedAverage.EWMA, Is.GreaterThan(0));
      Assert.That(weightedAverage.EWMA, Is.LessThanOrEqualTo(10000));

      lastEWMA = currentEWMA;
    }
  }
}