using PwrDrvr.LambdaDispatch.Router.EmbeddedMetrics;

namespace PwrDrvr.LambdaDispatch.Router.EmbeddedMetrics.Tests;

[TestFixture]
public class MetricsLoggerTests
{
  private MetricsLogger _metricsLogger;

  [SetUp]
  public void SetUp()
  {
    _metricsLogger = new MetricsLogger("TestNamespace");
  }

  [Test]
  public void PutMetric_ValidInput_Success()
  {
    Assert.DoesNotThrow(() => _metricsLogger.PutMetric("TestMetric", 1.0, Unit.Count));
  }

  [Test]
  public void PutMetric_Overflow_Channel()
  {
    for (int i = 0; i < 1000; i++)
    {
      Assert.DoesNotThrow(() => _metricsLogger.PutMetric("TestMetric", 1.0, Unit.Count));
    }
    _metricsLogger.Dispose();

    Thread.Sleep(1000);
  }

  [Test]
  public void Flush_Success()
  {
    // Wait 1 second for flush to run first time
    Thread.Sleep(1000);
    Assert.DoesNotThrow(() => _metricsLogger.PutMetric("TestMetric", 1.0, Unit.Count));
  }

  [Test]
  public void MetricsLogger_Dimensions_Valid()
  {
    var dimensions = new Dictionary<string, string>();
    for (int i = 0; i < 5; i++)
    {
      dimensions.Add($"key{i}", $"value{i}");
    }

    Assert.DoesNotThrow(() => new MetricsLogger("TestNamespace", dimensions));
  }

  [Test]
  public void MetricsLogger_TooManyDimensions_ThrowsException()
  {
    var dimensions = new Dictionary<string, string>();
    for (int i = 0; i < 31; i++)
    {
      dimensions.Add($"key{i}", $"value{i}");
    }

    Assert.Throws<ArgumentException>(() => new MetricsLogger("TestNamespace", dimensions));
  }

  [Test]
  public void MetricsLogger_InvalidDimensionType_ThrowsException()
  {
    var dimensions = new Dictionary<string, string>();
    for (int i = 0; i < 10; i++)
    {
#pragma warning disable CS8625 // Cannot convert null literal to non-nullable reference type.
      dimensions.Add($"key{i}", null);
#pragma warning restore CS8625 // Cannot convert null literal to non-nullable reference type.
    }

    Assert.Throws<ArgumentException>(() => new MetricsLogger("TestNamespace", dimensions));
  }

  [Test]
  public void MetricsLogger_DimensionValueTooLong_ThrowsException()
  {
    var dimensions = new Dictionary<string, string>
            {
                { "key", new string('a', 1025) }
            };

    Assert.Throws<ArgumentException>(() => new MetricsLogger("TestNamespace", dimensions));
  }

  [Test]
  public void Dispose_CancelsBackgroundTask()
  {
    _metricsLogger.Dispose();

    Assert.Throws<ObjectDisposedException>(() => _metricsLogger.PutMetric("TestMetric", 1.0, Unit.Count));
  }
}