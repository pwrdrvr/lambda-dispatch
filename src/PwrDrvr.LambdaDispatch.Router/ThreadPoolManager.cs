using System.Diagnostics;

namespace PwrDrvr.LambdaDispatch.Router;

public class ThreadPoolManager
{
  private readonly ILogger<ThreadPoolManager> _logger = LoggerInstance.CreateLogger<ThreadPoolManager>();

  private int _throughput;
  private int _lastThroughput;
  private int _lastMaxWorkerThreads;
  private double _lastCpuUsage;

  public ThreadPoolManager()
  {
    ThreadPool.SetMinThreads(1, 1);
    ThreadPool.GetMaxThreads(out _lastMaxWorkerThreads, out _);

    if (Environment.GetEnvironmentVariable("LAMBDA_DISPATCH_ThreadPoolTuner") == "true")
    {
      Task.Run(() => MonitorCpuAndThroughput());
    }
  }

  public void IndicateProduction()
  {
    Interlocked.Increment(ref _throughput);
  }

  private async Task MonitorCpuAndThroughput()
  {
    var lastProcessorTime = CrossPlatformProcessTime.GetTotalProcessorTimeMilliseconds();

    while (true)
    {
      await Task.Delay(TimeSpan.FromSeconds(5));

      ThreadPool.GetMaxThreads(out var maxWorkerThreads, out var ioCompletionPortThreads);

      var currentProcessorTime = CrossPlatformProcessTime.GetTotalProcessorTimeMilliseconds();
      var cpuUsageDelta = currentProcessorTime - lastProcessorTime;
      lastProcessorTime = currentProcessorTime;

      _logger.LogInformation("Total Processor Time: {currentProcessorTime} ms", currentProcessorTime);

      _logger.LogInformation("CPU usage delta: {cpuUsageDelta}", cpuUsageDelta);

      // Calculate the number of threads that would be 100% busy.
      int busyThreads = (int)(cpuUsageDelta / 5000.0);

      _logger.LogInformation("Busy threads: {busyThreads}", busyThreads);

      // Calculate the number of threads needed to keep the average utilization at 70%.
      int optimalMaxWorkerThreads = Math.Max((int)(busyThreads / 0.9), 3);

      double cpuUsagePerMaxThreadAvg = (cpuUsageDelta / maxWorkerThreads) / 5000.0 * 100.0;

      if (maxWorkerThreads > optimalMaxWorkerThreads)
      {
        _logger.LogInformation("Adjusting max worker threads from {maxWorkerThreads} to {optimalMaxWorkerThreads}", maxWorkerThreads, optimalMaxWorkerThreads);
        ThreadPool.SetMaxThreads(optimalMaxWorkerThreads, ioCompletionPortThreads);
        maxWorkerThreads = optimalMaxWorkerThreads;
      }

      if (cpuUsagePerMaxThreadAvg > 90 && _throughput > _lastThroughput)
      {
        _logger.LogInformation("Increasing max worker threads from {maxWorkerThreads} to {maxWorkerThreadsPlusOne}", maxWorkerThreads, maxWorkerThreads + 1);
        ThreadPool.SetMaxThreads(maxWorkerThreads + 1, ioCompletionPortThreads);
      }
      else if (cpuUsagePerMaxThreadAvg < 90 && _throughput >= _lastThroughput * 0.9 && maxWorkerThreads > 1)
      {
        _logger.LogInformation("Decreasing max worker threads from {maxWorkerThreads} to {maxWorkerThreadsMinusOne}", maxWorkerThreads, maxWorkerThreads - 1);
        ThreadPool.SetMaxThreads(maxWorkerThreads - 1, ioCompletionPortThreads);
      }
      else if (_lastCpuUsage > 90 && _throughput < _lastThroughput && maxWorkerThreads > _lastMaxWorkerThreads)
      {
        _logger.LogInformation("Reverting max worker threads from {maxWorkerThreads} to {lastMaxWorkerThreads}", maxWorkerThreads, _lastMaxWorkerThreads);
        ThreadPool.SetMaxThreads(_lastMaxWorkerThreads, ioCompletionPortThreads);
      }

      _lastCpuUsage = cpuUsagePerMaxThreadAvg;
      _lastThroughput = _throughput;
      _lastMaxWorkerThreads = maxWorkerThreads;
      _throughput = 0;
    }
  }
}