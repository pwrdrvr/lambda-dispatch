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
      await sw.WriteLineAsync($"{meter.Name}: {meter.Value.Count} count {Math.Round(meter.Value.MeanRate, 1)} mean rate {Math.Round(meter.Value.OneMinuteRate, 1)} 1 min {Math.Round(meter.Value.FiveMinuteRate, 1)} 5 min {Math.Round(meter.Value.FifteenMinuteRate, 1)} 15 min {meter.Unit}");
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

public static class MetricsRegistry
{
  public static readonly IMetricsRoot Metrics = new MetricsBuilder()
    .Report.Using<LoggerMetricsReporter>()
    .Build();

  static MetricsRegistry()
  {
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
    MeasurementUnit = Unit.Requests
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
    MeasurementUnit = Unit.Requests
  };

  public static readonly CounterOptions QueuedRequests = new()
  {
    Name = "QueuedRequests",
    MeasurementUnit = Unit.Requests
  };

  public static readonly CounterOptions RunningRequests = new()
  {
    Name = "RunningRequests",
    MeasurementUnit = Unit.Requests
  };

  public static readonly CounterOptions PendingDispatchCount = new()
  {
    Name = "PendingDispatchCount",
    MeasurementUnit = Unit.Requests
  };

  public static readonly CounterOptions PendingDispatchForegroundCount = new()
  {
    Name = "PendingDispatchForegroundCount",
    MeasurementUnit = Unit.Requests
  };

  public static readonly CounterOptions PendingDispatchBackgroundCount = new()
  {
    Name = "PendingDispatchBackgroundCount",
    MeasurementUnit = Unit.Requests
  };

  /// <summary>
  /// Number of Lambda.InvokeAsync calls outstanding
  /// </summary>
  public static readonly CounterOptions LambdaInvokeCount = new()
  {
    Name = "LambdaInvokeCount",
    MeasurementUnit = Unit.Items,
  };

  public static readonly GaugeOptions LambdaInstanceStartingCount = new()
  {
    Name = "LambdaInstanceStartingCount",
    MeasurementUnit = Unit.Items,
  };

  public static readonly GaugeOptions IncomingRequestRPS = new()
  {
    Name = "IncomingRequestRPS",
    MeasurementUnit = Unit.Requests,
  };

  public static readonly GaugeOptions LambdaInstanceStoppingCount = new()
  {
    Name = "LambdaInstanceStoppingCount",
    MeasurementUnit = Unit.Items,
  };

  public static readonly GaugeOptions LambdaInstanceRunningCount = new()
  {
    Name = "LambdaInstanceRunningCount",
    MeasurementUnit = Unit.Items,
  };

  public static readonly GaugeOptions LambdaInstanceDesiredCount = new()
  {
    Name = "LambdaInstanceDesiredCount",
    MeasurementUnit = Unit.Items,
  };

  public static readonly CounterOptions LambdaConnectionRejectedCount = new()
  {
    Name = "LambdaConnectionRejectedCount",
    MeasurementUnit = Unit.Connections,
  };

  public static readonly HistogramOptions LambdaInstanceOpenConnections = new()
  {
    Name = "LambdaInstanceOpenConnections",
    MeasurementUnit = Unit.Requests,
    Reservoir = () => new DefaultAlgorithmRReservoir(),
  };

  public static readonly HistogramOptions LambdaInstanceRunningRequests = new()
  {
    Name = "LambdaInstanceRunningRequests",
    MeasurementUnit = Unit.Requests,
    Reservoir = () => new DefaultAlgorithmRReservoir(),
  };

  public static readonly MeterOptions IncomingRequestsMeter = new()
  {
    Name = "IncomingRequests",
    MeasurementUnit = Unit.Requests,
    RateUnit = TimeUnit.Seconds,
  };

  public static async Task PrintMetrics()
  {
    while (true)
    {
      await Task.WhenAll(MetricsRegistry.Metrics.ReportRunner.RunAllAsync()).ConfigureAwait(false);
      await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    }
  }
}