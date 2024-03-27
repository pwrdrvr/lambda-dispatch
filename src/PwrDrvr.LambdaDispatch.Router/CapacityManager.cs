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

    var pendingDispatchCount = (double)cleanPendingRequests / _maxConcurrentCount / 2;

    var targetCount = (int)Math.Ceiling(requiredRunningCount + pendingDispatchCount);

    // Make sure we have 20% more capacity
    if (targetCount < (requiredRunningCount + pendingDispatchCount) * 1.2)
    {
      targetCount = (int)Math.Ceiling((requiredRunningCount + pendingDispatchCount) * 1.2);
    }

    return targetCount;
  }

  public int EwmaDesiredInstanceCount(double requestsPerSecondEWMA, double requestDurationEWMA, int currentDesiredInstanceCount)
  {
    double requestsPerSecondPerLambda
      = 1000 / Math.Max(2, requestDurationEWMA) * _targetConcurrentRequestsPerInstance;

    var ewmaScalerDesiredInstanceCount
      = (int)Math.Ceiling(requestsPerSecondEWMA / requestsPerSecondPerLambda * 1.2);

    if (ewmaScalerDesiredInstanceCount == currentDesiredInstanceCount - 1)
    {
      ewmaScalerDesiredInstanceCount = currentDesiredInstanceCount;
    }

    return ewmaScalerDesiredInstanceCount;
  }

  public int ConstrainDesiredInstanceCount(int proposedDesiredInstanceCount, int currentDesiredInstanceCount,
    int maxScaleOut, double maxScaleOutRatio, double maxScaleInRatio)
  {
    // Calculate the maximum allowed change
    int maxScaleOutChange
      = Math.Max(maxScaleOut, (int)Math.Ceiling(currentDesiredInstanceCount * maxScaleOutRatio));
    int maxScaleInChange
      = Math.Max(maxScaleOut, (int)Math.Ceiling(currentDesiredInstanceCount * maxScaleInRatio));

    // We always allow scaling to 0
    if (proposedDesiredInstanceCount == 0)
    {
      return 0;
    }

    // Calculate the proposed change
    int proposedChange = proposedDesiredInstanceCount - currentDesiredInstanceCount;

    // If the proposed change is greater than the maximum allowed change, adjust proposedDesiredInstanceCount
    if (proposedChange > maxScaleOutChange)
    {
      proposedDesiredInstanceCount = currentDesiredInstanceCount + maxScaleOutChange;
    }

    // If the proposed change is less than the negative of the maximum allowed change, adjust proposedDesiredInstanceCount
    if (proposedChange < -maxScaleInChange)
    {
      proposedDesiredInstanceCount = currentDesiredInstanceCount - maxScaleInChange;
    }

    return proposedDesiredInstanceCount;
  }
}