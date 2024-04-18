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

  public MetadataService(IHttpClientFactory? httpClientFactory = null, IConfig config = null)
  {
    var execEnvType = GetExecEnvType(config);

    if (config != null && !string.IsNullOrWhiteSpace(config.RouterCallbackHost))
    {
      _networkIP = config.RouterCallbackHost;
      _clusterName = null;
    }
    else if (execEnvType == IpSourceType.Local)
    {
      _networkIP = "127.0.0.1";
      _clusterName = null;
      return;
    }
    else if (execEnvType == IpSourceType.EnvVar)
    {
      // Works for EKS and other scenarios
      // Set the pod IP as an environment variable in the pod spec
      // and specify the name of the environment variable in the config
      // https://docs.aws.amazon.com/eks/latest/userguide/pod-configuration.html
      var CALLBACK_IP = Environment.GetEnvironmentVariable(config.EnvVarForCallbackIp);

      if (string.IsNullOrWhiteSpace(CALLBACK_IP))
      {
        throw new ApplicationException(string.Format("Failed to find callback ip from env var: ${0}", config.EnvVarForCallbackIp));
      }

      _networkIP = CALLBACK_IP;
      _clusterName = null;
    }
    else if (execEnvType == IpSourceType.ECS)
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

      response = _client.GetStringAsync($"{Environment.GetEnvironmentVariable("ECS_CONTAINER_METADATA_URI_V4")}/task", cts.Token).GetAwaiter().GetResult();
      metadata = JsonDocument.Parse(response).RootElement;
      var taskArnParts = metadata.GetProperty("TaskARN").GetString().Split('/');
      _clusterName = taskArnParts.Length > 1 ? taskArnParts[1] : "";
    }
    else if (execEnvType == IpSourceType.EC2)
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

  private enum IpSourceType
  {
    EC2,
    ECS,
    EnvVar,
    Local
  }

  private static IpSourceType GetExecEnvType(IConfig config)
  {
    if (config.EnvVarForCallbackIp != null
        && !string.IsNullOrWhiteSpace(config.EnvVarForCallbackIp)
        && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(config.EnvVarForCallbackIp)))
    {
      return IpSourceType.EnvVar;
    }

    var AWS_EXECUTION_ENV = Environment.GetEnvironmentVariable("AWS_EXECUTION_ENV");
    if (AWS_EXECUTION_ENV == null)
    {
      return IpSourceType.Local;
    }

    return AWS_EXECUTION_ENV.StartsWith("AWS_ECS") ? IpSourceType.ECS : IpSourceType.EC2;
  }
}