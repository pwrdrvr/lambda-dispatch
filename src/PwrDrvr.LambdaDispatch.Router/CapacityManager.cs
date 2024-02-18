namespace PwrDrvr.LambdaDispatch.Router;

public class CapacityManager(int maxConcurrentCount, int instanceCountMultiplier)
{
  private readonly int _maxConcurrentCount = maxConcurrentCount;
  private readonly int _instanceCountMultiplier = instanceCountMultiplier;
  private readonly int _targetConcurrentRequestsPerInstance
    = (int)Math.Ceiling((double)maxConcurrentCount / instanceCountMultiplier);

  /// <summary>
  /// Calculate the desired instance count based on the number of pending and running requests
  /// </summary>
  /// <param name="pendingRequests"></param>
  /// <param name="runningRequests"></param>
  /// <returns></returns>
  public int SimpleDesiredInstanceCount(int pendingRequests, int runningRequests)
  {
    // Calculate the desired count
    var cleanPendingRequests = Math.Max(pendingRequests, 0);
    var cleanRunningRequests = Math.Max(runningRequests, 0);

    // Special case for 0 pending or running requests
    if (cleanPendingRequests == 0 && cleanRunningRequests == 0)
    {
      return 0;
    }

    // Calculate the desired count
    // We have to have enough capacity for the currently running requests
    // For running requests we want to try to keep the instances at their target concurrent requests
    var requiredRunningCount
      = (double)cleanRunningRequests / _targetConcurrentRequestsPerInstance;

    // We want to be able to dispatch a portion of the pending requests
    // For pending requests, the dispacher will chew up all the connections, not just the target ones
    // Thus we scale based on the total connections for pending not the target
    // If we really start building a queue the EWMA scaler will kick in
    // Also as these shift over to running they will cause further scale up if they stick around
    var pendingDispatchCount
      = (double)cleanPendingRequests / _maxConcurrentCount * .5;

    return (int)Math.Ceiling(requiredRunningCount + pendingDispatchCount);
  }

  public int EwmaDesiredInstanceCount(double requestsPerSecondEWMA, double requestDurationEWMA)
  {
    double requestsPerSecondPerLambda
      = 1000 / Math.Max(2, requestDurationEWMA) * _targetConcurrentRequestsPerInstance;

    var ewmaScalerDesiredInstanceCount
      = (int)Math.Ceiling(requestsPerSecondEWMA / requestsPerSecondPerLambda);

    return ewmaScalerDesiredInstanceCount;
  }

  // public bool ManageCapacity(out int desiredInstanceCount,
  //   bool readAllMessages,
  //   int pendingRequests, int runningRequests,
  //   double requestsPerSecondEWMA, double requestDurationEWMA)
  // {
  //   desiredInstanceCount = 0;
  //   int? deferredScaleInNewDesiredInstanceCount = null;

  //   //
  //   // If we read all the messages and the pending/running is 0, then we can scale down to 0
  //   // Else default to being able to run all current requests and dispatch 50% of the pending requests
  //   //
  //   var newDesiredInstanceCount = (readAllMessages && pendingRequests == 0 && runningRequests == 0)
  //       ? 0 : (int)Math.Ceiling(averageCount);
  //   var simpleScalerDesiredInstanceCount = newDesiredInstanceCount;
  //   int? ewmaScalerDesiredInstanceCount = null;
  //   double? requestsPerSecondPerLambda = null;

  //   // Override the simple scale-from zero logic (which uses running and pending requests only)
  //   // with the EWMA data, if available
  //   if (requestsPerSecondEWMA > 0 && requestDurationEWMA > 0)
  //   {
  //     // Calculate the desired count
  //     var targetConcurrentRequestsPerInstance = (int)Math.Ceiling((double)_maxConcurrentCount / _instanceCountMultiplier);
  //     requestsPerSecondPerLambda = 1000 / requestDurationEWMA * targetConcurrentRequestsPerInstance;
  //     var oldDesiredInstanceCount = newDesiredInstanceCount;
  //     ewmaScalerDesiredInstanceCount = (int)Math.Ceiling(requestsPerSecondEWMA / (double)requestsPerSecondPerLambda);
  //     newDesiredInstanceCount = (int)ewmaScalerDesiredInstanceCount;
  //   }

  //   // This was a temp hack before removing the 10 ms sleep in the background dispatcher
  //   // newDesiredInstanceCount = (int)Math.Ceiling((double)newDesiredInstanceCount / 4);

  //   //
  //   // Locking instance counts - Do not do anything Async / IO in this block
  //   //
  //   lock (_instanceCountLock)
  //   {
  //     // Calculate the maximum allowed change
  //     int maxScaleOutChange = Math.Max(maxScaleOut, (int)Math.Ceiling(_desiredInstanceCount * maxScaleOutPercent));
  //     int maxScaleInChange = Math.Max(maxScaleOut, (int)Math.Ceiling(_desiredInstanceCount * maxScaleInPercent));

  //     // Calculate the proposed change
  //     int proposedChange = newDesiredInstanceCount - _desiredInstanceCount;

  //     // If the proposed change is greater than the maximum allowed change, adjust newDesiredInstanceCount
  //     if (proposedChange > maxScaleOutChange)
  //     {
  //       newDesiredInstanceCount = _desiredInstanceCount + maxScaleOutChange;
  //     }

  //     // If the proposed change is less than the negative of the maximum allowed change, adjust newDesiredInstanceCount
  //     if (proposedChange < -maxScaleInChange)
  //     {
  //       newDesiredInstanceCount = _desiredInstanceCount - maxScaleInChange;
  //     }

  //     // If we are already at the desired count and we have no starting instances, then we are done
  //     if (!deferredScaleInNewDesiredInstanceCount.HasValue
  //         && newDesiredInstanceCount == _desiredInstanceCount
  //         && _startingInstanceCount + _runningInstanceCount - _stoppingInstanceCount <= _desiredInstanceCount)
  //     {
  //       // Clear any deferred scale down
  //       deferredScaleInNewDesiredInstanceCount = null;

  //       // Restore any missing instances
  //       if (_startingInstanceCount + _runningInstanceCount - _stoppingInstanceCount < _desiredInstanceCount)
  //       {
  //         _logger.LogInformation("ManageCapacity - Desired count unchanged, restoring {} missing instances: _startingInstanceCount {} + _runningInstanceCount {} - _stoppingInstanceCount {} -> _desiredInstanceCount {}",
  //                _desiredInstanceCount - (_startingInstanceCount + _runningInstanceCount - _stoppingInstanceCount), _startingInstanceCount, _runningInstanceCount, _stoppingInstanceCount, _desiredInstanceCount); ;

  //         while (_startingInstanceCount + _runningInstanceCount - _stoppingInstanceCount < _desiredInstanceCount)
  //         {
  //           // Start a new instance
  //           StartNewInstance();
  //         }
  //       }

  //       // Nothing to do
  //       continue;
  //     }

  //     if (deferredScaleInNewDesiredInstanceCount.HasValue
  //       && deferredScaleInNewDesiredInstanceCount.Value == newDesiredInstanceCount)
  //     {
  //       // We are already scheduled to scale to this count
  //       continue;
  //     }

  //     // The current scale is already correct
  //     if (newDesiredInstanceCount == _desiredInstanceCount)
  //     {
  //       // The new desired count is the same as the current count
  //       // We have no need to scale down if we were going to
  //       // We also have no need to scale up
  //       deferredScaleInNewDesiredInstanceCount = null;
  //       continue;
  //     }

  //     // See if we are allowed to adjust the desired count
  //     // We are always allowed to scale out from 0 without consuming a token
  //     // We are always allowed to scale in to 0 without consuming a token
  //     var allowScaleWithoutToken = _desiredInstanceCount == 0 && newDesiredInstanceCount > 0;
  //     if (allowScaleWithoutToken
  //         // Allowing unlimited ins gets way too chatty
  //         // || (_desiredInstanceCount > 0 && newDesiredInstanceCount == 0)
  //         || scaleTokenBucket.TryGetToken())
  //     {
  //       var prevDesiredInstanceCount = _desiredInstanceCount;

  //       _metricsLogger.PutMetric("PendingRequestCount", pendingRequests, Unit.Count);
  //       _metricsLogger.PutMetric("RunningRequestCount", runningRequests, Unit.Count);

  //       // Start instances if needed
  //       if (newDesiredInstanceCount > _desiredInstanceCount)
  //       {
  //         _logger.LogInformation("ManageCapacity - Performing scale out: _desiredInstanceCount {_desiredInstanceCount} -> {newDesiredInstanceCount}, ewmaScalerDesiredInstanceCount {ewmaScalerDesiredInstanceCount}, simpleScalerDesiredInstanceCount {simpleScalerDesiredInstanceCount}, prevDesiredInstanceCount {prevDesiredInstanceCount}, requestsPerSecondEWMA {requestsPerSecondEWMA}, requestDurationEWMA {requestDurationEWMA}, requestsPerSecondPerLambda {requestsPerSecondPerLambda}",
  //                 _desiredInstanceCount, newDesiredInstanceCount, ewmaScalerDesiredInstanceCount != null ? ewmaScalerDesiredInstanceCount.Value : null, simpleScalerDesiredInstanceCount, prevDesiredInstanceCount, Math.Round(requestsPerSecondEWMA, 1), Math.Round(requestDurationEWMA, 1), Math.Round(requestsPerSecondPerLambda != null ? requestsPerSecondPerLambda.Value : Double.NaN, 1));

  //         // Clear any deferred scale down
  //         deferredScaleInNewDesiredInstanceCount = null;

  //         // Try to set the new desired count
  //         // Because we own setting this value we can do a simple compare and swap
  //         _desiredInstanceCount = newDesiredInstanceCount;

  //         // Record the new desired count
  //         MetricsRegistry.Metrics.Measure.Gauge.SetValue(MetricsRegistry.LambdaInstanceDesiredCount, _desiredInstanceCount);
  //         _metricsLogger.PutMetric("LambdaDesiredCount", newDesiredInstanceCount, Unit.Count);

  //         var scaleOutCount = Math.Max(newDesiredInstanceCount - (_runningInstanceCount + _startingInstanceCount - _stoppingInstanceCount), 0);
  //         scaleOutCount = Math.Min(scaleOutCount, maxScaleOut);
  //         scaleOutCount = Math.Min(scaleOutCount, (int)Math.Ceiling(_desiredInstanceCount * maxScaleOutPercent));
  //         if (scaleOutCount > 0)
  //         {
  //           _metricsLogger.PutMetric("LambdaScaleOutCount", scaleOutCount, Unit.Count);
  //         }
  //         while (scaleOutCount-- > 0)
  //         {
  //           // Start a new instance
  //           StartNewInstance();
  //         }
  //       }
  //       else if (!deferredScaleInNewDesiredInstanceCount.HasValue
  //               && newDesiredInstanceCount < _desiredInstanceCount)
  //       {
  //         _logger.LogDebug("ManageCapacity - Scheduling scale in: _desiredInstanceCount {_desiredInstanceCount} -> {newDesiredInstanceCount}, ewmaScalerDesiredInstanceCount {ewmaScalerDesiredInstanceCount}, simpleScalerDesiredInstanceCount {simpleScalerDesiredInstanceCount}, prevDesiredInstanceCount {prevDesiredInstanceCount}, requestsPerSecondEWMA {requestsPerSecondEWMA}, requestDurationEWMA {requestDurationEWMA}, requestsPerSecondPerLambda {requestsPerSecondPerLambda}, _runningInstanceCount {}, _startingInstanceCount {}, _stoppingInstanceCount {}",
  //                _desiredInstanceCount, newDesiredInstanceCount, ewmaScalerDesiredInstanceCount != null ? ewmaScalerDesiredInstanceCount.Value : null, simpleScalerDesiredInstanceCount, prevDesiredInstanceCount, Math.Round(requestsPerSecondEWMA, 1), Math.Round(requestDurationEWMA, 1), Math.Round(requestsPerSecondPerLambda != null ? requestsPerSecondPerLambda.Value : Double.NaN, 1), _runningInstanceCount, _startingInstanceCount, _stoppingInstanceCount);

  //         // Schedule a scale down
  //         deferredScaleInNewDesiredInstanceCount = newDesiredInstanceCount;
  //         cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(125));
  //       }
  //       else if (deferredScaleInNewDesiredInstanceCount.GetValueOrDefault() > newDesiredInstanceCount)
  //       {
  //         _logger.LogInformation("ManageCapacity - Adjusting scheduled scale in: _desiredInstanceCount {_desiredInstanceCount} -> {newDesiredInstanceCount}, ewmaScalerDesiredInstanceCount {ewmaScalerDesiredInstanceCount}, simpleScalerDesiredInstanceCount {simpleScalerDesiredInstanceCount}, prevDesiredInstanceCount {prevDesiredInstanceCount}, requestsPerSecondEWMA {requestsPerSecondEWMA}, requestDurationEWMA {requestDurationEWMA}, requestsPerSecondPerLambda {requestsPerSecondPerLambda}",
  //                              _desiredInstanceCount, newDesiredInstanceCount, ewmaScalerDesiredInstanceCount != null ? ewmaScalerDesiredInstanceCount.Value : null, simpleScalerDesiredInstanceCount, prevDesiredInstanceCount, Math.Round(requestsPerSecondEWMA, 1), Math.Round(requestDurationEWMA, 1), Math.Round(requestsPerSecondPerLambda != null ? requestsPerSecondPerLambda.Value : Double.NaN, 1));

  //         // Leave the schedule but adjust the amount
  //         deferredScaleInNewDesiredInstanceCount = newDesiredInstanceCount;
  //       }
  //       else
  //       {
  //         _logger.LogInformation("ManageCapacity - Cancelling scheduled scale in: _desiredInstanceCount {_desiredInstanceCount} -> {newDesiredInstanceCount}, ewmaScalerDesiredInstanceCount {ewmaScalerDesiredInstanceCount}, simpleScalerDesiredInstanceCount {simpleScalerDesiredInstanceCount}, prevDesiredInstanceCount {prevDesiredInstanceCount}, requestsPerSecondEWMA {requestsPerSecondEWMA}, requestDurationEWMA {requestDurationEWMA}, requestsPerSecondPerLambda {requestsPerSecondPerLambda}",
  //                              _desiredInstanceCount, newDesiredInstanceCount, ewmaScalerDesiredInstanceCount != null ? ewmaScalerDesiredInstanceCount.Value : null, simpleScalerDesiredInstanceCount, prevDesiredInstanceCount, Math.Round(requestsPerSecondEWMA, 1), Math.Round(requestDurationEWMA, 1), Math.Round(requestsPerSecondPerLambda != null ? requestsPerSecondPerLambda.Value : Double.NaN, 1));

  //         // Cancel the scale down
  //         deferredScaleInNewDesiredInstanceCount = null;
  //         cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
  //       }
  //     }
  //   }
  // }
}