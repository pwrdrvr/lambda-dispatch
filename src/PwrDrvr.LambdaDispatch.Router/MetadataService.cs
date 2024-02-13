using System.Text.Json;
using Amazon.Util;

namespace PwrDrvr.LambdaDispatch.Router;

public interface IMetadataService
{
  string NetworkIP { get; }
  string? ClusterName { get; }
}

public class MetadataService : IMetadataService
{
  private readonly string _networkIP;
  private readonly string? _clusterName;

  public string NetworkIP => _networkIP;
  public string? ClusterName => _clusterName;

  public MetadataService(IHttpClientFactory? httpClientFactory = null)
  {
    var execEnvType = GetExecEnvType();

    if (execEnvType == ExecEnvType.Local)
    {
      _networkIP = "127.0.0.1";
      _clusterName = null;
      return;
    }
    else if (execEnvType == ExecEnvType.ECS)
    {
      // https://docs.aws.amazon.com/AmazonECS/latest/developerguide/task-metadata-endpoint-v4.html
      var _client = httpClientFactory != null ? httpClientFactory.CreateClient() : new HttpClient();
      using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
      var response = _client.GetStringAsync(Environment.GetEnvironmentVariable("ECS_CONTAINER_METADATA_URI_V4"), cts.Token).GetAwaiter().GetResult();
      var metadata = JsonDocument.Parse(response).RootElement;

      // ECS response
      var networks = metadata.GetProperty("Networks");
      foreach (var network in networks.EnumerateArray())
      {
        if (network.GetProperty("NetworkMode").GetString() == "awsvpc")
        {
          _networkIP = network.GetProperty("IPv4Addresses").EnumerateArray().First().GetString();
          break;
        }
      }

      var taskArnParts = metadata.GetProperty("TaskARN").GetString().Split('/');
      _clusterName = taskArnParts.Length > 1 ? taskArnParts[1] : "";
    }
    else if (execEnvType == ExecEnvType.EC2)
    {
      // Running on EC2
      _networkIP = EC2InstanceMetadata.NetworkInterfaces.First().LocalIPv4s.First();
      _clusterName = null;
    }

    if (_networkIP == null)
    {
      throw new ApplicationException("Failed to find awsvpc network");
    }
  }

  private enum ExecEnvType
  {
    EC2,
    ECS,
    Local
  }

  private static ExecEnvType GetExecEnvType()
  {
    var AWS_EXECUTION_ENV = Environment.GetEnvironmentVariable("AWS_EXECUTION_ENV");
    if (AWS_EXECUTION_ENV == null)
    {
      return ExecEnvType.Local;
    }

    // TODO: Add EKS check

    return AWS_EXECUTION_ENV.StartsWith("AWS_ECS") ? ExecEnvType.ECS : ExecEnvType.EC2;
  }
}