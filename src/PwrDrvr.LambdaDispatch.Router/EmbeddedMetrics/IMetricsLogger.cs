namespace PwrDrvr.LambdaDispatch.Router.EmbeddedMetrics;

public interface IMetricsLogger
{
  void PutMetric(string name, double value, Unit unit);
}
