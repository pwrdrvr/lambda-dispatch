using System.Text.Json;
using System.Threading.Channels;

namespace PwrDrvr.LambdaDispatch.Router.EmbeddedMetrics;

public class MetricsLogger : IMetricsLogger, IDisposable
{
  private class Metric
  {
    public string Name { get; set; } = string.Empty;
    public double Value { get; set; }
    public Unit Unit { get; set; }
  }

  private class MetricData
  {
    public Unit Unit { get; set; }
    public List<double> Values { get; set; } = [];
  }

  private readonly Channel<Metric> _channel;
  private readonly string _namespace;
  private readonly Dictionary<string, MetricData> _metrics = [];
  private readonly Dictionary<string, string> _dimensions;
  private readonly object _flushLock = new();
  private DateTime _currentMinute;
  private CancellationTokenSource _cancellationTokenSource;

  public MetricsLogger(string metricsNamespace, Dictionary<string, string>? dimensions = null)
  {
    _namespace = metricsNamespace;
    _channel = Channel.CreateBounded<Metric>(new BoundedChannelOptions(10000)
    {
      FullMode = BoundedChannelFullMode.DropOldest
    });
    if (dimensions != null)
    {
      ValidateDimensions(dimensions);
    }
    _dimensions = dimensions ?? [];

    var now = DateTime.UtcNow;
    _currentMinute = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0);

    _cancellationTokenSource = new CancellationTokenSource();
    StartFlushTask();

    Task.Run(ReadFromChannel, cancellationToken: _cancellationTokenSource.Token);
  }

  public void PutMetric(string name, double value, Unit unit)
  {
    if (_cancellationTokenSource.Token.IsCancellationRequested)
    {
      throw new ObjectDisposedException(nameof(MetricsLogger));
    }

    _channel.Writer.TryWrite(new Metric { Name = name, Value = value, Unit = unit });
  }

  public void Dispose()
  {
    _cancellationTokenSource.Cancel();
    _cancellationTokenSource.Dispose();
    GC.SuppressFinalize(this);
  }

  private void UpdateCurrentMinute()
  {
    var now = DateTime.UtcNow;
    var minute = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0);
    _currentMinute = minute;
  }

  private void StartFlushTask()
  {
    Task.Run(async () =>
    {
      while (!_cancellationTokenSource.Token.IsCancellationRequested)
      {
        var now = DateTime.UtcNow;
        var nextMinute = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0).AddMinutes(1);
        var delay = nextMinute - now;

        await Task.Delay(delay, _cancellationTokenSource.Token);

        lock (_flushLock)
        {
          Flush();
          UpdateCurrentMinute();
        }
      }
    }, cancellationToken: _cancellationTokenSource.Token);
  }

  private async Task ReadFromChannel()
  {
    await foreach (var metric in _channel.Reader.ReadAllAsync(_cancellationTokenSource.Token))
    {
      if (!_metrics.TryGetValue(metric.Name, out var metricData))
      {
        metricData = new MetricData { Unit = metric.Unit };
        _metrics[metric.Name] = metricData;
      }

      metricData.Values.Add(metric.Value);

      if (metricData.Values.Count >= 100)
      {
        lock (_flushLock)
        {
          FlushMetric(metric.Name, metricData);
        }
      }
    }
  }

  /// <summary>
  /// NOT thread safe - acquire the flush lock before calling
  /// </summary>
  private void Flush()
  {
    foreach (var pair in _metrics)
    {
      FlushMetric(pair.Key, pair.Value);
    }

    _metrics.Clear();
  }

  /// <summary>
  /// NOT thread safe - acquire the flush lock before calling
  /// </summary>
  private void FlushMetric(string name, MetricData metricData)
  {
    // This can happen if we went to flush a single metric in ReadFromChannel but blocked on the
    // lock, after which out metric was cleared of all values
    if (metricData.Values.Count == 0)
    {
      return;
    }

    // Only write if we have non-zero values
    if (metricData.Values.Any(v => v != 0))
    {
      var timestamp = (long)(_currentMinute - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
      var emfData = new Dictionary<string, object>
      {
        ["_aws"] = new
        {
          Timestamp = timestamp,
          CloudWatchMetrics = new[]
              {
                new
                {
                    Namespace = _namespace,
                    Dimensions = new[] { _dimensions.Select(d => d.Key).ToArray() },
                    Metrics = new[]
                    {
                        new
                        {
                            Name = name,
                            // https://docs.aws.amazon.com/AmazonCloudWatch/latest/monitoring/CloudWatch_Embedded_Metric_Format_Specification.html#CloudWatch_Embedded_Metric_Format_Specification_structure_metricdefinition
                            // https://docs.aws.amazon.com/AmazonCloudWatch/latest/APIReference/API_MetricDatum.html
                            Unit = metricData.Unit.ToString(),
                        }
                    }
                }
            }
        },
        [name] = metricData.Values,
      };
      foreach (var dimension in _dimensions)
      {
        emfData[dimension.Key] = dimension.Value;
      }

      string json = JsonSerializer.Serialize(emfData);
      Console.WriteLine(json);
    }

    // Always clear the metric values
    metricData.Values.Clear();
  }

  private static void ValidateDimensions(Dictionary<string, string> dimensions)
  {
    const int maxDimensions = 30;
    const int maxDimensionValueLength = 1024;

    if (dimensions.Count > maxDimensions)
    {
      throw new ArgumentException($"Too many dimensions. The maximum number of dimensions is {maxDimensions}.");
    }

    foreach (var dimension in dimensions)
    {
      if (dimension.Value == null || !(dimension.Value is string))
      {
        throw new ArgumentException($"Invalid dimension value. All dimension values must be strings.");
      }

      if (dimension.Value.Length > maxDimensionValueLength)
      {
        throw new ArgumentException($"Dimension value too long. The maximum length for a dimension value is {maxDimensionValueLength} characters.");
      }
    }
  }
}