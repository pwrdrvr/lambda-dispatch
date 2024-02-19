namespace PwrDrvr.LambdaDispatch.Router;

public interface IShutdownSignal
{
  public CancellationTokenSource Shutdown { get; }
}

public class ShutdownSignal : IShutdownSignal
{
  public CancellationTokenSource Shutdown { get; private set; } = new();
}
