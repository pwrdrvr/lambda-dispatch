using Microsoft.Extensions.Configuration;
using NuGet.Frameworks;

namespace PwrDrvr.LambdaDispatch.Router.Tests;

public class ConfigTests
{
  [Test]
  public void TestCreateAndValidate_ValidFunctionName()
  {
    var inMemorySettings = new Dictionary<string, string?> {
        {"FunctionName", "my-function"},
    };
    IConfiguration configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(inMemorySettings)
        .Build();
    var config = Config.CreateAndValidate(configuration);
    Assert.Multiple(() =>
    {
      Assert.That(config.FunctionName, Is.EqualTo("my-function"));
      Assert.That(config.FunctionNameOnly, Is.EqualTo("my-function"));
      Assert.That(config.FunctionNameQualifier, Is.Null);
    });
  }

  [Test]
  public void TestCreateAndValidate_ValidFunctionNameWithQualifier()
  {
    var inMemorySettings = new Dictionary<string, string?> {
        {"FunctionName", "my-function:qualifier"},
    };
    IConfiguration configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(inMemorySettings)
        .Build();
    var config = Config.CreateAndValidate(configuration);
    Assert.Multiple(() =>
    {
      Assert.That(config.FunctionName, Is.EqualTo("my-function:qualifier"));
      Assert.That(config.FunctionNameOnly, Is.EqualTo("my-function"));
      Assert.That(config.FunctionNameQualifier, Is.EqualTo("qualifier"));
    });
  }

  [Test]
  public void TestCreateAndValidate_ValidLambdaArn()
  {
    var inMemorySettings = new Dictionary<string, string?> {
        {"FunctionName", "arn:aws:lambda:us-west-2:123456789012:function:my-function"},
    };
    IConfiguration configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(inMemorySettings)
        .Build();
    var config = Config.CreateAndValidate(configuration);
    Assert.Multiple(() =>
    {
      Assert.That(config.FunctionName, Is.EqualTo("arn:aws:lambda:us-west-2:123456789012:function:my-function"));
      Assert.That(config.FunctionNameOnly, Is.EqualTo("arn:aws:lambda:us-west-2:123456789012:function:my-function"));
      Assert.That(config.FunctionNameQualifier, Is.Null);
    });
  }

  [Test]
  public void TestCreateAndValidate_ValidLambdaArnWithQualifier()
  {
    var inMemorySettings = new Dictionary<string, string?> {
        {"FunctionName", "arn:aws:lambda:us-west-2:123456789012:function:my-function:qualifier"},
    };
    IConfiguration configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(inMemorySettings)
        .Build();
    var config = Config.CreateAndValidate(configuration);
    Assert.Multiple(() =>
    {
      Assert.That(config.FunctionName, Is.EqualTo("arn:aws:lambda:us-west-2:123456789012:function:my-function:qualifier"));
      Assert.That(config.FunctionNameOnly, Is.EqualTo("arn:aws:lambda:us-west-2:123456789012:function:my-function"));
      Assert.That(config.FunctionNameQualifier, Is.EqualTo("qualifier"));
    });
  }

  [Test]
  public void TestCreateAndValidate_InvalidFunctionName_ThrowsException()
  {
    var inMemorySettings = new Dictionary<string, string?> {
        {"FunctionName", "invalid function name"},
    };
    IConfiguration configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(inMemorySettings)
        .Build();

    Assert.Throws<ApplicationException>(() => Config.CreateAndValidate(configuration));
  }

  [TestCase("MaxConcurrentCount", "-1", typeof(ApplicationException))]
  [TestCase("MaxConcurrentCount", "101", typeof(ApplicationException))]
  [TestCase("ChannelCount", "-2", typeof(ApplicationException))]
  [TestCase("ChannelCount", "102", typeof(ApplicationException))]
  [TestCase("IncomingRequestHTTPPort", "-5001", typeof(ApplicationException))]
  [TestCase("IncomingRequestHTTPPort", "75001", typeof(ApplicationException))]
  [TestCase("IncomingRequestHTTPSPort", "-5002", typeof(ApplicationException))]
  [TestCase("IncomingRequestHTTPSPort", "75002", typeof(ApplicationException))]
  [TestCase("ControlChannelInsecureHTTP2Port", "-5003", typeof(ApplicationException))]
  [TestCase("ControlChannelInsecureHTTP2Port", "75003", typeof(ApplicationException))]
  [TestCase("ControlChannelHTTP2Port", "-5004", typeof(ApplicationException))]
  [TestCase("ControlChannelHTTP2Port", "75004", typeof(ApplicationException))]
  [TestCase("AllowInsecureControlChannel", "not-a-boolean", typeof(InvalidOperationException))]
  [TestCase("CloudWatchMetricsEnabled", "not-a-boolean", typeof(InvalidOperationException))]
  [TestCase("PreferredControlChannelScheme", "not-a-scheme", typeof(ApplicationException))]
  [TestCase("InstanceCountMultiplier", "-2", typeof(ApplicationException))]
  [TestCase("InstanceCountMultiplier", "11", typeof(ApplicationException))]
  [TestCase("EnvVarForCallbackIp", "@", typeof(ApplicationException))]
  public void TestCreateAndValidate_InvalidSettings(string settingKey, string settingValue, Type expectedExceptionType)
  {
    var inMemorySettings = new Dictionary<string, string?> {
        { "FunctionName", "my-function"},
        {settingKey, settingValue},
    };
    IConfiguration configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(inMemorySettings)
        .Build();
    // Assert that CreateAndValidate throws the expected exception
    Assert.Throws(expectedExceptionType, () => Config.CreateAndValidate(configuration));
  }

  [TestCase("MaxConcurrentCount", "50")]
  [TestCase("ChannelCount", "50")]
  [TestCase("IncomingRequestHTTPPort", "15001")]
  [TestCase("IncomingRequestHTTPSPort", "15002")]
  [TestCase("ControlChannelInsecureHTTP2Port", "15003")]
  [TestCase("ControlChannelHTTP2Port", "15004")]
  [TestCase("AllowInsecureControlChannel", "true")]
  [TestCase("AllowInsecureControlChannel", "false")]
  [TestCase("CloudWatchMetricsEnabled", "true")]
  [TestCase("CloudWatchMetricsEnabled", "false")]
  [TestCase("PreferredControlChannelScheme", "https")]
  [TestCase("InstanceCountMultiplier", "2")]
  [TestCase("EnvVarForCallbackIp", "CALLBACK_IP")]
  public void TestCreateAndValidate_ValidSettings(string settingKey, string settingValue)
  {
    var inMemorySettings = new Dictionary<string, string?> {
        { "FunctionName", "my-function"},
        {settingKey, settingValue},
    };
    IConfiguration configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(inMemorySettings)
        .Build();

    var config = Config.CreateAndValidate(configuration);

    Assert.That(config.GetType().GetProperty(settingKey).GetValue(config).ToString().ToLower(), Is.EqualTo(settingValue.ToLower()));
  }

  [Test]
  public void TestCreateAndValidate_InvalidSettings_AllowInsecureControlChannelFalse()
  {
    var inMemorySettings = new Dictionary<string, string?> {
        { "FunctionName", "my-function"},
        { "AllowInsecureControlChannel", "false"},
        { "PreferredControlChannelScheme", "http" },
    };
    IConfiguration configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(inMemorySettings)
        .Build();
    // Assert that CreateAndValidate throws the expected exception
    Assert.Throws(typeof(ApplicationException), () => Config.CreateAndValidate(configuration));
  }

  [Test]
  public void TestCreateAndValidate_AllowInsecureControlChannelTrue()
  {
    var inMemorySettings = new Dictionary<string, string?> {
        { "FunctionName", "my-function"},
        { "AllowInsecureControlChannel", "true"},
        { "PreferredControlChannelScheme", "http" },
    };
    IConfiguration configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(inMemorySettings)
        .Build();
    var config = Config.CreateAndValidate(configuration);
    Assert.Multiple(() =>
    {
      Assert.That(config.PreferredControlChannelScheme, Is.EqualTo("http"));
      Assert.That(config.AllowInsecureControlChannel, Is.True);
    });
  }

  [TestCase("simple", ScalingAlgorithms.Simple)]
  [TestCase("Simple", ScalingAlgorithms.Simple)]
  [TestCase("ewma", ScalingAlgorithms.EWMA)]
  [TestCase("EWMA", ScalingAlgorithms.EWMA)]
  public void TestScalingAlgorithm_Valid(string scalingAlgorithm, ScalingAlgorithms expectedScalingAlgorithm)
  {
    var inMemorySettings = new Dictionary<string, string?> {
        { "FunctionName", "my-function"},
        { "ScalingAlgorithm", scalingAlgorithm},
    };
    IConfiguration configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(inMemorySettings)
        .Build();
    var config = Config.CreateAndValidate(configuration);
    Assert.Multiple(() =>
    {
      Assert.That(config.ScalingAlgorithm, Is.EqualTo(scalingAlgorithm));
      Assert.That(config.ScalingAlgorithmEnum, Is.EqualTo(expectedScalingAlgorithm));
    });
  }

  [Test]
  public void TestScalingAlgorithm_Invalid()
  {
    var inMemorySettings = new Dictionary<string, string?> {
        { "FunctionName", "my-function"},
        { "ScalingAlgorithm", "cats"},
    };
    IConfiguration configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(inMemorySettings)
        .Build();
    Assert.Throws<ArgumentException>(() => Config.CreateAndValidate(configuration));
  }
}