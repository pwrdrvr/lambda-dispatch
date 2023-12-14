namespace PwrDrvr.LambdaDispatch.Router;

using System.IO;
using System.Text;
using App.Metrics;
using App.Metrics.Counter;
using App.Metrics.Formatters;
using App.Metrics.Gauge;
using App.Metrics.Histogram;
using App.Metrics.ReservoirSampling.Uniform;
using App.Metrics.Scheduling;
using App.Metrics.Timer;

public class CompactMetricsFormatter : IMetricsOutputFormatter
{
  public MetricsMediaTypeValue MediaType => new MetricsMediaTypeValue("text", "vnd.appmetrics.metrics.compact", "v1", "plain");

  MetricFields IMetricsOutputFormatter.MetricFields { get; set; } = new MetricFields();

  public async Task WriteAsync(Stream output, MetricsDataValueSource metricsData, CancellationToken cancellationToken = default)
  {
    using var sw = new StreamWriter(output);
    var timestamp = DateTime.Now.ToString("HH:mm:ss.fff ");

    foreach (var gauge in metricsData.Contexts.SelectMany(context => context.Gauges))
    {
      await sw.WriteLineAsync($"{timestamp} {gauge.Name}: {gauge.Value} {gauge.Unit}");
    }

    foreach (var counter in metricsData.Contexts.SelectMany(context => context.Counters))
    {
      await sw.WriteLineAsync($"{timestamp} {counter.Name}: {counter.Value.Count} {counter.Unit}");
    }

    foreach (var histogram in metricsData.Contexts.SelectMany(context => context.Histograms))
    {
      await sw.WriteLineAsync($"{timestamp} {histogram.Name}: {histogram.Value.Count} count {Math.Round(histogram.Value.LastValue, 1)} last {Math.Round(histogram.Value.Mean, 1)} mean {Math.Round(histogram.Value.Min, 1)} min {Math.Round(histogram.Value.Max, 1)} max {histogram.Unit}");
    }

    foreach (var timer in metricsData.Contexts.SelectMany(context => context.Timers))
    {
      await sw.WriteLineAsync($"{timestamp} {timer.Name}: {timer.Value.Histogram.Count} count {Math.Round(timer.Value.Histogram.LastValue, 1)} last {Math.Round(timer.Value.Histogram.Mean, 1)} mean {Math.Round(timer.Value.Histogram.Min, 1)} min {Math.Round(timer.Value.Histogram.Max, 1)} max {timer.Unit}");
    }
  }
}

public static class MetricsRegistry
{
  public static readonly IMetricsRoot Metrics = new MetricsBuilder()
    .OutputMetrics.Using<CompactMetricsFormatter>()
    .Report.ToConsole()
    .Build();

  private static readonly AppMetricsTaskScheduler _scheduler = new(
#if DEBUG
    TimeSpan.FromSeconds(15),
#else
    TimeSpan.FromSeconds(10),
#endif
    async () =>
    {
      await Task.WhenAll(Metrics.ReportRunner.RunAllAsync());
    });

  static MetricsRegistry()
  {
    // Start the console logger
    _scheduler.Start();
  }

  public static void Reset()
  {
    Metrics.Manage.Reset();
  }

  public static readonly HistogramOptions DispatchDelay = new()
  {
    Name = "DispatchDelay",
    MeasurementUnit = Unit.Custom("ms"),
    Reservoir = () => new DefaultAlgorithmRReservoir(),
  };

  public static readonly HistogramOptions LambdaOpenDelay = new()
  {
    Name = "LambdaOpenDelay",
    MeasurementUnit = Unit.Custom("ms"),
    Reservoir = () => new DefaultAlgorithmRReservoir(),
  };

  public static readonly CounterOptions RequestCount = new()
  {
    Name = "RequestCount",
    MeasurementUnit = Unit.Custom("requests"),
  };

  public static readonly TimerOptions IncomingRequestTimer = new()
  {
    Name = "IncomingRequestTimer",
    MeasurementUnit = Unit.Custom("ms"),
    DurationUnit = TimeUnit.Milliseconds,
    RateUnit = TimeUnit.Milliseconds,
  };

  public static readonly TimerOptions LambdaRequestTimer = new()
  {
    Name = "LambdaRequestTimer",
    MeasurementUnit = Unit.Custom("ms"),
    DurationUnit = TimeUnit.Milliseconds,
    RateUnit = TimeUnit.Milliseconds,
  };

  public static readonly CounterOptions ImmediateDispatchCount = new()
  {
    Name = "ImmediateDispatchCount",
    MeasurementUnit = Unit.Custom("requests"),
  };

  public static readonly CounterOptions QueuedRequests = new()
  {
    Name = "QueuedRequests",
    MeasurementUnit = Unit.Custom("requests"),
  };

  public static readonly CounterOptions RunningRequests = new()
  {
    Name = "RunningRequests",
    MeasurementUnit = Unit.Custom("requests"),
  };

  public static readonly CounterOptions PendingDispatchCount = new()
  {
    Name = "PendingDispatchCount",
    MeasurementUnit = Unit.Custom("requests"),
  };

  public static readonly CounterOptions PendingDispatchForegroundCount = new()
  {
    Name = "PendingDispatchForegroundCount",
    MeasurementUnit = Unit.Custom("requests"),
  };

  public static readonly CounterOptions PendingDispatchBackgroundCount = new()
  {
    Name = "PendingDispatchBackgroundCount",
    MeasurementUnit = Unit.Custom("requests"),
  };

  public static readonly CounterOptions LambdaInstanceCount = new()
  {
    Name = "LambdaInstanceCount",
    MeasurementUnit = Unit.Custom("instances"),
  };

  public static readonly GaugeOptions LambdaInstanceStartingCount = new()
  {
    Name = "LambdaInstanceStartingCount",
    MeasurementUnit = Unit.Custom("instances"),
  };

  public static readonly GaugeOptions LambdaInstanceRunningCount = new()
  {
    Name = "LambdaInstanceRunningCount",
    MeasurementUnit = Unit.Custom("instances"),
  };

  public static readonly GaugeOptions LambdaInstanceDesiredCount = new()
  {
    Name = "LambdaInstanceDesiredCount",
    MeasurementUnit = Unit.Custom("instances"),
  };

  public static readonly CounterOptions LambdaConnectionRejectedCount = new()
  {
    Name = "LambdaConnectionRejectedCount",
    MeasurementUnit = Unit.Custom("connections"),
  };

  public static readonly HistogramOptions LambdaInstanceOpenConnections = new()
  {
    Name = "LambdaInstanceOpenConnections",
    MeasurementUnit = Unit.Custom("requests"),
    Reservoir = () => new DefaultAlgorithmRReservoir(),
  };

  public static readonly HistogramOptions LambdaInstanceRunningRequests = new()
  {
    Name = "LambdaInstanceRunningRequests",
    MeasurementUnit = Unit.Custom("requests"),
    Reservoir = () => new DefaultAlgorithmRReservoir(),
  };
}