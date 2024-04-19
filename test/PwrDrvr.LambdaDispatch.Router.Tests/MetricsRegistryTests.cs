using NUnit.Framework;
using App.Metrics;
using App.Metrics.Counter;
using App.Metrics.Histogram;
using App.Metrics.Timer;

namespace PwrDrvr.LambdaDispatch.Router.Tests;

[TestFixture]
public class MetricsRegistryTests
{
  private MetricsRegistry _metricsRegistry;

  [SetUp]
  public void SetUp()
  {
    _metricsRegistry = new MetricsRegistry();
  }

  [Test]
  public void MetricsRegistry_CounterOptions_Success()
  {
    CounterOptions counterOption = _metricsRegistry.RequestCount;
    Assert.That(counterOption, Is.Not.Null);
    Assert.Multiple(() =>
    {
      Assert.That(counterOption.Name, Is.EqualTo("RequestCount"));
      Assert.That(counterOption.MeasurementUnit, Is.EqualTo(Unit.Requests));
    });
  }

  [Test]
  public void MetricsRegistry_HistogramOptions_Success()
  {
    HistogramOptions histogramOption = _metricsRegistry.DispatchDelay;
    Assert.That(histogramOption, Is.Not.Null);
    Assert.Multiple(() =>
    {
      Assert.That(histogramOption.Name, Is.EqualTo("DispatchDelay"));
      Assert.That(histogramOption.MeasurementUnit, Is.EqualTo(Unit.Custom("ms")));
    });
  }

  [Test]
  public void MetricsRegistry_TimerOptions_Success()
  {
    TimerOptions timerOption = _metricsRegistry.LambdaRequestTimer;
    Assert.That(timerOption, Is.Not.Null);
    Assert.Multiple(() =>
    {
      Assert.That(timerOption.Name, Is.EqualTo("LambdaRequestTimer"));
      Assert.That(timerOption.MeasurementUnit, Is.EqualTo(Unit.Custom("ms")));
    });
  }

  [Test]
  public void MetricsRegistry_Reset_Success()
  {
    Assert.DoesNotThrow(() => _metricsRegistry.Reset());
  }
}