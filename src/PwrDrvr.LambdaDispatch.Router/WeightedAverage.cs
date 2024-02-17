namespace PwrDrvr.LambdaDispatch.Router;

using System.Diagnostics;
using App.Metrics.Concurrency;

public class WeightedAverage
{
  private readonly StripedLongAdder _value = new();
  private readonly StripedLongAdder _count = new();

  private readonly List<(int timestamp, long value, long count)> _data = [];
  private readonly int _windowSizeInSeconds;
  private const double Alpha = 0.1;
  private double _ewma;
  private readonly bool _mean;
  private readonly Stopwatch _sw = new();

  /// <summary>
  /// Get the Exponential Weighted Moving Average that is computed in the background
  /// </summary>
  public double EWMA { get { return _ewma; } }

  /// <summary>
  /// Thread safe computation of EWMA of values or EWMA of mean of values
  /// </summary>
  /// <param name="windowSizeInSeconds"></param>
  /// <param name="mean">Compute EWMA of mean of values</param>
  public WeightedAverage(int windowSizeInSeconds, bool mean = false)
  {
    _mean = mean;
    _windowSizeInSeconds = windowSizeInSeconds;
    _sw.Start();

    // Start a task to compute the average
    Task.Run(async () =>
    {
      while (true)
      {
        try
        {
          var now = _sw.Elapsed;
          var nextTickMilliseconds = ((now.TotalMilliseconds / 100) + 1) * 100;
          var delay = nextTickMilliseconds - now.TotalMilliseconds;
          await Task.Delay((int)delay);

          CleanupOldData();

          now = _sw.Elapsed;
          var currentSecond = (int)now.TotalSeconds;
          var proportionOfSecondElapsed = now.TotalMilliseconds % 1000 / 1000;
          var currentValue = _value.GetAndReset();
          var currentCount = _count.GetAndReset();

          // If the list is empty or the current second is different from the first second in the list,
          // insert a new item at the beginning of the list
          if (_data.Count == 0 || _data[0].timestamp != currentSecond)
          {
            _data.Insert(0, (currentSecond, currentValue, currentCount));
          }
          else
          {
            // Otherwise, update the count for the current second
            var first = _data[0];
            _data[0] = (first.timestamp, first.value + currentValue, first.count + currentCount);
          }

          double ewma = _mean ? 0.0 : double.NaN;

          // Calculate the EWMA of the counts per second
          var index = 0;
          foreach (var (_, value, count) in _data)
          {
            if (index == 0)
            {
              // Extrapolate data for the current second
              if (!_mean)
              {
                var adj = value / proportionOfSecondElapsed;

                // Start off with the first value at weight of 100%
                ewma = adj;
              }
              else
              {
                // Mean is always value / count
                // Note: this is the duration and count of requests
                // that finished in this second, not the count of incoming
                // requests and duration of other requests that finished
                if (count > 0)
                {
                  ewma = value / count;
                }
              }
            }
            else
            {
              // Use the actual count for the previous seconds
              if (!_mean)
              {
                ewma = Alpha * value + (1 - Alpha) * ewma;
              }
              else
              {
                if (count > 0)
                {
                  ewma = Alpha * (value / count) + (1 - Alpha) * ewma;
                }
              }
            }

            index++;
          }

          _ewma = ewma;
        }
        catch (Exception ex)
        {
          Console.WriteLine($"Exception in WeightedAverage: {ex.Message}");
        }
      }
    });
  }

  private void CleanupOldData()
  {
    var now = _sw.Elapsed;
    var currentSecond = (int)now.TotalSeconds;

    // Remove items that are older than _windowSizeInSeconds from the end of the list
    // We keep an extra data point for the current second, which is prorated
    while (_data.Count > 0 && (currentSecond - _data[^1].timestamp) > _windowSizeInSeconds + 1)
    {
      _data.RemoveAt(_data.Count - 1);
    }

    // Remove runs of zeros from the oldest data points
    while (_data.Count > 1 && _data[^1].value == 0)
    {
      _data.RemoveAt(_data.Count - 1);
    }
  }

  public void Add(long count = 1)
  {
    _value.Add(count);
    if (_mean)
    {
      _count.Add(1);
    }
  }
}