namespace PwrDrvr.LambdaDispatch.Router.Tests;

[TestFixture]
public class TrailingAverageTests
{
  [Test]
  public void Test_Add_And_Average()
  {
    var trailingAverage = new TrailingAverage();

    trailingAverage.Add(1);
    Assert.That(trailingAverage.Average, Is.EqualTo(1));

    trailingAverage.Add(2);
    Assert.That(trailingAverage.Average, Is.EqualTo(1.5));

    trailingAverage.Add(3);
    Assert.That(trailingAverage.Average, Is.EqualTo(2));
  }

  [Test]
  public void Test_Average_With_Old_Data()
  {
    var trailingAverage = new TrailingAverage();

    trailingAverage.Add(1);
    trailingAverage.Add(2);
    trailingAverage.Add(3);

    // Wait for 6 seconds to make sure the old data is cleaned up
    Thread.Sleep(6000);

    Assert.That(trailingAverage.Average, Is.EqualTo(0));

    trailingAverage.Add(4);
    Assert.That(trailingAverage.Average, Is.EqualTo(4));
  }
}