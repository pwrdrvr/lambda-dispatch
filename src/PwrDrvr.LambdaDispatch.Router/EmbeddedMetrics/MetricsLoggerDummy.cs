namespace PwrDrvr.LambdaDispatch.Router.EmbeddedMetrics;

public class MetricsLoggerDummy : IMetricsLogger
{
  public void PutMetric(string name, double value, Unit unit)
  {
  }
}