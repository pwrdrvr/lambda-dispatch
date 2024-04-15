namespace PwrDrvr.LambdaDispatch.Router;

public interface IPoolOptions
{
  /// <summary>
  /// The ARN, name, or name:qualifier of the Lambda function to be invoked
  /// </summary>
  string LambdaName { get; }

  /// <summary>
  /// The ID of the pool, used for the Extension request to get routed back to the correct pool
  /// </summary>
  string PoolId { get; }

  public void Setup(string lambdaName, string poolId);
}


public class PoolOptions : IPoolOptions
{
  public void Setup(string lambdaName, string poolId)
  {
    if (LambdaName != null || PoolId != null)
    {
      throw new InvalidOperationException("PoolOptions has already been setup");
    }

    LambdaName = lambdaName;
    PoolId = poolId;
  }

  public string LambdaName { get; private set; }

  public string PoolId { get; private set; }
}