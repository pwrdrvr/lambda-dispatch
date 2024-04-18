namespace PwrDrvr.LambdaDispatch.Router;

using System.IO;
using System.Text;
using App.Metrics;
using App.Metrics.Counter;
using App.Metrics.Filters;
using App.Metrics.Formatters;
using App.Metrics.Formatters.Ascii;
using App.Metrics.Gauge;
using App.Metrics.Histogram;
using App.Metrics.Meter;
using App.Metrics.Reporting;
using App.Metrics.ReservoirSampling.Uniform;
using App.Metrics.Scheduling;
using App.Metrics.Timer;

public class LoggerMetricsReporter : IReportMetrics
{
  private readonly ILogger _logger = LoggerInstance.CreateLogger<LoggerMetricsReporter>();

  public IFilterMetrics Filter { get; set; }
  public TimeSpan FlushInterval { get; set; }
  public IMetricsOutputFormatter Formatter { get; set; }

  private readonly IMetricsOutputFormatter _defaultMetricsOutputFormatter = new MetricsTextOutputFormatter();

  public LoggerMetricsReporter()
  {
    // FlushInterval = AppMetricsConstants.Reporting.DefaultFlushInterval;
    FlushInterval = TimeSpan.FromSeconds(10);
    Formatter = new CompactMetricsFormatter();
    // Formatter = _defaultMetricsOutputFormatter;
  }

  public void Dispose()
  {
  }

  public async Task<bool> FlushAsync(MetricsDataValueSource metricsData, CancellationToken cancellationToken = default)
  {
    using (var stream = new MemoryStream())
    {
      await Formatter.WriteAsync(stream, metricsData, cancellationToken);

      var output = Encoding.UTF8.GetString(stream.ToArray());

      _logger.LogInformation("Metrics:\n{output}", output);
    }

    return true;
  }
}

public class CompactMetricsFormatter : IMetricsOutputFormatter
{
  public MetricsMediaTypeValue MediaType => new MetricsMediaTypeValue("text", "vnd.appmetrics.metrics.compact", "v1", "plain");

  MetricFields IMetricsOutputFormatter.MetricFields { get; set; } = new MetricFields();

  public async Task WriteAsync(Stream output, MetricsDataValueSource metricsData, CancellationToken cancellationToken = default)
  {
    using var ms = new MemoryStream();
    using var sw = new StreamWriter(ms);

    foreach (var gauge in metricsData.Contexts.SelectMany(context => context.Gauges))
    {
      await sw.WriteLineAsync($"{gauge.Name}: {Math.Round(gauge.Value, 1)} {gauge.Unit}");
    }

    foreach (var counter in metricsData.Contexts.SelectMany(context => context.Counters))
    {
      await sw.WriteLineAsync($"{counter.Name}: {counter.Value.Count} {counter.Unit}");
    }

    foreach (var histogram in metricsData.Contexts.SelectMany(context => context.Histograms))
    {
      await sw.WriteLineAsync($"{histogram.Name}: {histogram.Value.Count} count {Math.Round(histogram.Value.LastValue, 1)} last {Math.Round(histogram.Value.Mean, 1)} mean {Math.Round(histogram.Value.Min, 1)} min {Math.Round(histogram.Value.Max, 1)} max {Math.Round(histogram.Value.Percentile95, 1)} 95p {histogram.Unit}");
    }

    foreach (var timer in metricsData.Contexts.SelectMany(context => context.Timers))
    {
      await sw.WriteLineAsync($"{timer.Name}: {timer.Value.Histogram.Count} count {Math.Round(timer.Value.Histogram.LastValue, 1)} last {Math.Round(timer.Value.Histogram.Mean, 1)} mean {Math.Round(timer.Value.Histogram.Min, 1)} min {Math.Round(timer.Value.Histogram.Max, 1)} max {timer.Unit}");
    }

    foreach (var meter in metricsData.Contexts.SelectMany(context => context.Meters))
    {
      await sw.WriteLineAsync($"{meter.Name}: {meter.Value.Count} count {Math.Round(meter.Value.MeanRate, 1)} mean rate {Math.Round(meter.Value.OneMinuteRate, 1)} 1min ewma {Math.Round(meter.Value.FiveMinuteRate, 1)} 5min ewma {Math.Round(meter.Value.FifteenMinuteRate, 1)} 15min ewma {meter.Unit}");
    }

    // Sort the metric text and write it to the output stream
    await sw.FlushAsync(cancellationToken);
    ms.Position = 0;
    var sr = new StreamReader(ms);
    var lines = await sr.ReadToEndAsync(cancellationToken);
    var sortedLines = lines.Split('\n').OrderBy(l => l);
    foreach (var line in sortedLines)
    {
      await output.WriteAsync(Encoding.UTF8.GetBytes(line), cancellationToken);
      await output.WriteAsync(Encoding.UTF8.GetBytes("\n"), cancellationToken);
    }
  }
}

public interface IMetricsRegistry
{
  IMetricsRoot Metrics { get; }
  HistogramOptions DispatchDelay { get; }
  HistogramOptions LambdaOpenDelay { get; }
  CounterOptions RequestCount { get; }
  HistogramOptions IncomingRequestDuration { get; }
  HistogramOptions IncomingRequestDurationAfterDispatch { get; }
  TimerOptions LambdaRequestTimer { get; }
  CounterOptions ImmediateDispatchCount { get; }
  CounterOptions QueuedRequests { get; }
  CounterOptions RunningRequests { get; }
  CounterOptions PendingDispatchCount { get; }
  CounterOptions PendingDispatchForegroundCount { get; }
  CounterOptions PendingDispatchBackgroundCount { get; }
  CounterOptions LambdaInvokeCount { get; }
  GaugeOptions LambdaInstanceStartingCount { get; }
  GaugeOptions IncomingRequestRPS { get; }
  GaugeOptions IncomingRequestDurationEWMA { get; }
  GaugeOptions LambdaInstanceStoppingCount { get; }
  GaugeOptions LambdaInstanceRunningCount { get; }
  GaugeOptions LambdaInstanceDesiredCount { get; }
  CounterOptions LambdaConnectionRejectedCount { get; }
  HistogramOptions LambdaInstanceOpenConnections { get; }
  HistogramOptions LambdaInstanceRunningRequests { get; }
  MeterOptions IncomingRequestsMeter { get; }
  void Reset();
  Task PrintMetrics();
}


public class MetricsRegistry : IMetricsRegistry
{
  public IMetricsRoot Metrics { get; } = new MetricsBuilder()
    .Report.Using<LoggerMetricsReporter>()
    .Build();

  public void Reset()
  {
    Metrics.Manage.Reset();
  }

  public HistogramOptions DispatchDelay { get; } = new()
  {
    Name = "DispatchDelay",
    MeasurementUnit = Unit.Custom("ms"),
    Reservoir = () => new DefaultAlgorithmRReservoir(),
  };

  public HistogramOptions LambdaOpenDelay { get; } = new()
  {
    Name = "LambdaOpenDelay",
    MeasurementUnit = Unit.Custom("ms"),
    Reservoir = () => new DefaultAlgorithmRReservoir(),
  };

  public CounterOptions RequestCount { get; } = new()
  {
    Name = "RequestCount",
    MeasurementUnit = Unit.Requests
  };

  public HistogramOptions IncomingRequestDuration { get; } = new()
  {
    Name = "IncomingRequestDuration",
    MeasurementUnit = Unit.Custom("ms"),
    Reservoir = () => new DefaultAlgorithmRReservoir(),
  };

  public HistogramOptions IncomingRequestDurationAfterDispatch { get; } = new()
  {
    Name = "IncomingRequestDurationAfterDispatch",
    MeasurementUnit = Unit.Custom("ms"),
    Reservoir = () => new DefaultAlgorithmRReservoir(),
  };

  public TimerOptions LambdaRequestTimer { get; } = new()
  {
    Name = "LambdaRequestTimer",
    MeasurementUnit = Unit.Custom("ms"),
    DurationUnit = TimeUnit.Milliseconds,
    RateUnit = TimeUnit.Milliseconds,
  };

  public CounterOptions ImmediateDispatchCount { get; } = new()
  {
    Name = "ImmediateDispatchCount",
    MeasurementUnit = Unit.Requests
  };

  public CounterOptions QueuedRequests { get; } = new()
  {
    Name = "QueuedRequests",
    MeasurementUnit = Unit.Requests
  };

  public CounterOptions RunningRequests { get; } = new()
  {
    Name = "RunningRequests",
    MeasurementUnit = Unit.Requests
  };

  public CounterOptions PendingDispatchCount { get; } = new()
  {
    Name = "PendingDispatchCount",
    MeasurementUnit = Unit.Requests
  };

  public CounterOptions PendingDispatchForegroundCount { get; } = new()
  {
    Name = "PendingDispatchForegroundCount",
    MeasurementUnit = Unit.Requests
  };

  public CounterOptions PendingDispatchBackgroundCount { get; } = new()
  {
    Name = "PendingDispatchBackgroundCount",
    MeasurementUnit = Unit.Requests
  };

  /// <summary>
  /// Number of Lambda.InvokeAsync calls outstanding
  /// </summary>
  public CounterOptions LambdaInvokeCount { get; } = new()
  {
    Name = "LambdaInvokeCount",
    MeasurementUnit = Unit.Items,
  };

  public GaugeOptions LambdaInstanceStartingCount { get; } = new()
  {
    Name = "LambdaInstanceStartingCount",
    MeasurementUnit = Unit.Items,
  };

  public GaugeOptions IncomingRequestRPS { get; } = new()
  {
    Name = "IncomingRequestRPS",
    MeasurementUnit = Unit.Requests,
  };

  public GaugeOptions IncomingRequestDurationEWMA { get; } = new()
  {
    Name = "IncomingRequestDurationEWMA",
    MeasurementUnit = Unit.Custom("ms"),
  };

  public GaugeOptions LambdaInstanceStoppingCount { get; } = new()
  {
    Name = "LambdaInstanceStoppingCount",
    MeasurementUnit = Unit.Items,
  };

  public GaugeOptions LambdaInstanceRunningCount { get; } = new()
  {
    Name = "LambdaInstanceRunningCount",
    MeasurementUnit = Unit.Items,
  };

  public GaugeOptions LambdaInstanceDesiredCount { get; } = new()
  {
    Name = "LambdaInstanceDesiredCount",
    MeasurementUnit = Unit.Items,
  };

  public CounterOptions LambdaConnectionRejectedCount { get; } = new()
  {
    Name = "LambdaConnectionRejectedCount",
    MeasurementUnit = Unit.Connections,
  };

  public HistogramOptions LambdaInstanceOpenConnections { get; } = new()
  {
    Name = "LambdaInstanceOpenConnections",
    MeasurementUnit = Unit.Requests,
    Reservoir = () => new DefaultAlgorithmRReservoir(),
  };

  public HistogramOptions LambdaInstanceRunningRequests { get; } = new()
  {
    Name = "LambdaInstanceRunningRequests",
    MeasurementUnit = Unit.Requests,
    Reservoir = () => new DefaultAlgorithmRReservoir(),
  };

  public MeterOptions IncomingRequestsMeter { get; } = new()
  {
    Name = "IncomingRequests",
    MeasurementUnit = Unit.Requests,
    RateUnit = TimeUnit.Seconds,
  };

  public async Task PrintMetrics()
  {
    while (true)
    {
      await Task.WhenAll(this.Metrics.ReportRunner.RunAllAsync()).ConfigureAwait(false);
      await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    }
  }
}


public static class MetricsRegistrySingleton
{
  private static IMetricsRegistry _instance = new MetricsRegistry();

  public static IMetricsRoot Metrics => _instance.Metrics;

  public static void Reset()
  {
    Metrics.Manage.Reset();
  }

  public static HistogramOptions DispatchDelay => _instance.DispatchDelay;

  public static HistogramOptions LambdaOpenDelay => _instance.LambdaOpenDelay;

  public static CounterOptions RequestCount => _instance.RequestCount;

  public static HistogramOptions IncomingRequestDuration => _instance.IncomingRequestDuration;

  public static HistogramOptions IncomingRequestDurationAfterDispatch => _instance.IncomingRequestDurationAfterDispatch;

  public static TimerOptions LambdaRequestTimer => _instance.LambdaRequestTimer;

  public static CounterOptions ImmediateDispatchCount => _instance.ImmediateDispatchCount;

  public static CounterOptions QueuedRequests => _instance.QueuedRequests;

  public static CounterOptions RunningRequests => _instance.RunningRequests;

  public static CounterOptions PendingDispatchCount => _instance.PendingDispatchCount;

  public static CounterOptions PendingDispatchForegroundCount => _instance.PendingDispatchForegroundCount;

  public static CounterOptions PendingDispatchBackgroundCount => _instance.PendingDispatchBackgroundCount;

  /// <summary>
  /// Number of Lambda.InvokeAsync calls outstanding
  /// </summary>
  public static CounterOptions LambdaInvokeCount => _instance.LambdaInvokeCount;

  public static GaugeOptions LambdaInstanceStartingCount => _instance.LambdaInstanceStartingCount;

  public static GaugeOptions IncomingRequestRPS => _instance.IncomingRequestRPS;

  public static GaugeOptions IncomingRequestDurationEWMA => _instance.IncomingRequestDurationEWMA;

  public static GaugeOptions LambdaInstanceStoppingCount => _instance.LambdaInstanceStoppingCount;

  public static GaugeOptions LambdaInstanceRunningCount => _instance.LambdaInstanceRunningCount;

  public static GaugeOptions LambdaInstanceDesiredCount => _instance.LambdaInstanceDesiredCount;

  public static CounterOptions LambdaConnectionRejectedCount => _instance.LambdaConnectionRejectedCount;

  public static HistogramOptions LambdaInstanceOpenConnections => _instance.LambdaInstanceOpenConnections;

  public static HistogramOptions LambdaInstanceRunningRequests => _instance.LambdaInstanceRunningRequests;

  public static MeterOptions IncomingRequestsMeter => _instance.IncomingRequestsMeter;

  public static async Task PrintMetrics()
  {
    await _instance.PrintMetrics();
  }
}