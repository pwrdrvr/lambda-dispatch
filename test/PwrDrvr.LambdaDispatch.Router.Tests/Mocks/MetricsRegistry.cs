using Moq;
using App.Metrics;
using App.Metrics.Counter;
using App.Metrics.Histogram;
using App.Metrics.Gauge;
using App.Metrics.Timer;

namespace PwrDrvr.LambdaDispatch.Router.Tests.Mocks;

public static class MetricsRegistryMockFactory
{
  public static Mock<IMetricsRegistry> Create()
  {
    var mockMetricsRegistry = new Mock<IMetricsRegistry>();
    var mockMetricsRoot = new Mock<IMetricsRoot>();
    var mockMeasure = new Mock<IMeasureMetrics>();
    var mockCounter = new Mock<IMeasureCounterMetrics>();
    var mockHistogram = new Mock<IMeasureHistogramMetrics>();
    var mockGauge = new Mock<IMeasureGaugeMetrics>();
    var mockTimer = new Mock<IMeasureTimerMetrics>();

    mockMetricsRegistry.Setup(mr => mr.Metrics).Returns(mockMetricsRoot.Object);
    mockMetricsRoot.Setup(mr => mr.Measure).Returns(mockMeasure.Object);
    mockMeasure.Setup(m => m.Counter).Returns(mockCounter.Object);
    mockMeasure.Setup(m => m.Histogram).Returns(mockHistogram.Object);
    mockMeasure.Setup(m => m.Gauge).Returns(mockGauge.Object);
    mockMeasure.Setup(m => m.Timer).Returns(mockTimer.Object);

    mockCounter.Setup(c => c.Increment(It.IsAny<CounterOptions>())).Verifiable();
    mockHistogram.Setup(h => h.Update(It.IsAny<HistogramOptions>(), It.IsAny<long>())).Verifiable();
    mockGauge.Setup(g => g.SetValue(It.IsAny<GaugeOptions>(), It.IsAny<double>())).Verifiable();
    mockTimer.Setup(t => t.Time(It.IsAny<TimerOptions>())).Returns(() => new TimerContext()).Verifiable();

    return mockMetricsRegistry;
  }
}