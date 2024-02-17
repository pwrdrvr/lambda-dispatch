namespace PwrDrvr.LambdaDispatch.Router;

using System.Diagnostics;
using App.Metrics.Concurrency;

public class WeightedAverage
{
  private readonly StripedLongAdder _uncounted = new();

  private readonly List<(int timestamp, long count)> _data = [];
  private readonly int _windowSizeInSeconds;
  private const double Alpha = 0.1;
  private double _ewma;
  private Stopwatch _sw = new();

  public double EWMA { get { return _ewma; } }

  public WeightedAverage(int windowSizeInSeconds)
  {
    _windowSizeInSeconds = windowSizeInSeconds;
    _sw.Start();

    // Start a task to compute the average
    Task.Run(async () =>
    {
      while (true)
      {
        var now = _sw.Elapsed;
        var nextTickMilliseconds = ((now.TotalMilliseconds / 100) + 1) * 100;
        var delay = nextTickMilliseconds - now.TotalMilliseconds;
        await Task.Delay((int)delay);

        CleanupOldData();

        now = _sw.Elapsed;
        var currentSecond = (int)now.TotalSeconds;
        var proportionOfSecondElapsed = now.TotalMilliseconds % 1000 / 1000;
        var currentCount = _uncounted.GetAndReset();

        // If the list is empty or the current second is different from the first second in the list,
        // insert a new item at the beginning of the list
        if (_data.Count == 0 || _data[0].timestamp != currentSecond)
        {
          _data.Insert(0, (currentSecond, currentCount));
        }
        else
        {
          // Otherwise, update the count for the current second
          var first = _data[0];
          _data[0] = (first.timestamp, first.count + currentCount);
        }

        double ewma = 0;

        // Calculate the EWMA of the counts per second
        var index = 0;
        foreach (var (_, count) in _data)
        {
          var value = 0.0;

          if (index == 0)
          {
            // Extrapolate data for the current second
            value = count / proportionOfSecondElapsed;

            // Start off with the first value at weight of 100%
            ewma = value;
          }
          else
          {
            // Use the actual count for the previous seconds
            value = count;
            ewma = Alpha * value + (1 - Alpha) * ewma;
          }

          index++;
        }

        _ewma = ewma;
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
    while (_data.Count > 1 && _data[^1].count == 0)
    {
      _data.RemoveAt(_data.Count - 1);
    }
  }

  public void Add(int count = 1)
  {
    _uncounted.Add(count);
  }
}