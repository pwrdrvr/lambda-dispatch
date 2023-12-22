namespace PwrDrvr.LambdaDispatch.LambdaLB;

#if !NATIVE_AOT

using System.IO;
using System.Text;
using App.Metrics;
using App.Metrics.Counter;
using App.Metrics.Filters;
using App.Metrics.Formatters;
using App.Metrics.Formatters.Ascii;
using App.Metrics.Gauge;
using App.Metrics.Reporting;
using App.Metrics.Timer;
using Microsoft.Extensions.Logging;

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

      _logger.LogInformation(output);
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
    using var sw = new StreamWriter(output);

    foreach (var gauge in metricsData.Contexts.SelectMany(context => context.Gauges))
    {
      await sw.WriteLineAsync($"{gauge.Name}: {gauge.Value} {gauge.Unit}");
    }

    foreach (var counter in metricsData.Contexts.SelectMany(context => context.Counters))
    {
      await sw.WriteLineAsync($"{counter.Name}: {counter.Value.Count} {counter.Unit}");
    }

    foreach (var histogram in metricsData.Contexts.SelectMany(context => context.Histograms))
    {
      await sw.WriteLineAsync($"{histogram.Name}: {histogram.Value.Count} count {Math.Round(histogram.Value.LastValue, 1)} last {Math.Round(histogram.Value.Mean, 1)} mean {Math.Round(histogram.Value.Min, 1)} min {Math.Round(histogram.Value.Max, 1)} max {histogram.Unit}");
    }

    foreach (var timer in metricsData.Contexts.SelectMany(context => context.Timers))
    {
      await sw.WriteLineAsync($"{timer.Name}: {timer.Value.Histogram.Count} count {Math.Round(timer.Value.Histogram.LastValue, 1)} last {Math.Round(timer.Value.Histogram.Mean, 1)} mean {Math.Round(timer.Value.Histogram.Min, 1)} min {Math.Round(timer.Value.Histogram.Max, 1)} max {timer.Unit}");
    }
  }
}

public static class MetricsRegistry
{
  public static readonly IMetricsRoot Metrics = new MetricsBuilder()
    // .Report.ToConsole(options =>
    // {
    //   options.FlushInterval = TimeSpan.FromSeconds(10);
    //   options.MetricsOutputFormatter = new CompactMetricsFormatter();
    // })
    .Report.Using<LoggerMetricsReporter>()
    .Build();

  static MetricsRegistry()
  {
    App.Metrics.Logging.LogProvider.IsDisabled = true;
  }

  public static readonly TimerOptions ChannelWaitDuration = new()
  {
    Name = "ChannelWaitDuration",
    MeasurementUnit = Unit.Custom("ms"),
    DurationUnit = TimeUnit.Milliseconds,
    RateUnit = TimeUnit.Seconds,
  };

  public static readonly CounterOptions ChannelsOpen = new()
  {
    Name = "ChannelsOpen",
    MeasurementUnit = Unit.Items
  };

  public static readonly CounterOptions ChannelsClosed = new()
  {
    Name = "ChannelsClosed",
    MeasurementUnit = Unit.Items
  };

  public static readonly CounterOptions RequestCount = new()
  {
    Name = "RequestCount",
    MeasurementUnit = Unit.Requests,
  };

  public static readonly CounterOptions RequestConflictCount = new()
  {
    Name = "RequestConflictCount",
    MeasurementUnit = Unit.Requests,
  };

  public static readonly CounterOptions RespondedCount = new()
  {
    Name = "RespondedCount",
    MeasurementUnit = Unit.Requests,
  };

  public static readonly TimerOptions IncomingRequestTimer = new()
  {
    Name = "IncomingRequestTimer",
    MeasurementUnit = Unit.Custom("ms"),
    DurationUnit = TimeUnit.Milliseconds,
    RateUnit = TimeUnit.Seconds,
  };

  public static readonly GaugeOptions LastWakeupTime = new()
  {
    Name = "LastWakeupTime",
    MeasurementUnit = Unit.Custom("ms"),
  };

  public static async Task PrintMetrics(CancellationToken cancellationToken)
  {
    while (!cancellationToken.IsCancellationRequested)
    {
      try
      {
        await Task.WhenAll(MetricsRegistry.Metrics.ReportRunner.RunAllAsync());
        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
      }
      catch (OperationCanceledException)
      {
        return;
      }
      catch (Exception ex)
      {
        Console.WriteLine(ex);
      }
    }
  }
}
#endif