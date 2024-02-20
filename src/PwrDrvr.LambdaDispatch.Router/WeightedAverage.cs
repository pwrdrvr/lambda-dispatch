namespace PwrDrvr.LambdaDispatch.Router;

using System.Diagnostics;
using App.Metrics.Concurrency;

public class WeightedAverage
{
  private readonly StripedLongAdder _value = new();
  private readonly StripedLongAdder _count = new();

  private readonly int _windowSizeInSeconds;
  private readonly double Alpha;
  private readonly int tickIntervalMs = 100;
  private double _ewma = 0;
  private readonly bool _mean;

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

    Alpha = 1 - Math.Exp(-tickIntervalMs / (_windowSizeInSeconds * 1000.0));

    // Start a task to compute the average
    Task.Run(async () =>
    {
      while (true)
      {
        try
        {
          await Task.Delay((int)tickIntervalMs);

          var currentValue = _value.GetAndReset();
          var currentCount = _count.GetAndReset();

          var instantRate = (double)currentValue / (tickIntervalMs / 1000.0);
          var instantMean = currentCount > 0 ? (double)currentValue / (double)currentCount : 0;

          // Calculate the EWMA of the counts per second
          if (_mean)
          {
            _ewma = Alpha * instantMean + (1 - Alpha) * _ewma;
          }
          else
          {
            _ewma = Alpha * instantRate + (1 - Alpha) * _ewma;
          }
        }
        catch (Exception ex)
        {
          Console.WriteLine($"Exception in WeightedAverage: {ex.Message}");
        }
      }
    });
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