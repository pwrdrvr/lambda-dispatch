using NUnit.Framework;
using Moq;
using Microsoft.Extensions.Configuration;

namespace PwrDrvr.LambdaDispatch.Router.Tests;

public class ConfigTests
{
  [Test]
  public void TestCreateAndValidate_ValidFunctionName()
  {
    var inMemorySettings = new Dictionary<string, string> {
        {"FunctionName", "my-function"},
    };
    IConfiguration configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(inMemorySettings)
        .Build();
    var config = Config.CreateAndValidate(configuration);

    Assert.That(config.FunctionName, Is.EqualTo("my-function"));
    Assert.That(config.FunctionNameOnly, Is.EqualTo("my-function"));
    Assert.That(config.FunctionNameQualifier, Is.Null);
  }

  [Test]
  public void TestCreateAndValidate_ValidFunctionNameWithQualifier()
  {
    var inMemorySettings = new Dictionary<string, string> {
        {"FunctionName", "my-function:qualifier"},
    };
    IConfiguration configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(inMemorySettings)
        .Build();
    var config = Config.CreateAndValidate(configuration);

    Assert.That(config.FunctionName, Is.EqualTo("my-function:qualifier"));
    Assert.That(config.FunctionNameOnly, Is.EqualTo("my-function"));
    Assert.That(config.FunctionNameQualifier, Is.EqualTo("qualifier"));
  }

  [Test]
  public void TestCreateAndValidate_ValidLambdaArn()
  {
    var inMemorySettings = new Dictionary<string, string> {
        {"FunctionName", "arn:aws:lambda:us-west-2:123456789012:function:my-function"},
    };
    IConfiguration configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(inMemorySettings)
        .Build();
    var config = Config.CreateAndValidate(configuration);

    Assert.That(config.FunctionName, Is.EqualTo("arn:aws:lambda:us-west-2:123456789012:function:my-function"));
    Assert.That(config.FunctionNameOnly, Is.EqualTo("arn:aws:lambda:us-west-2:123456789012:function:my-function"));
    Assert.That(config.FunctionNameQualifier, Is.Null);
  }

  [Test]
  public void TestCreateAndValidate_ValidLambdaArnWithQualifier()
  {
    var inMemorySettings = new Dictionary<string, string> {
        {"FunctionName", "arn:aws:lambda:us-west-2:123456789012:function:my-function:qualifier"},
    };
    IConfiguration configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(inMemorySettings)
        .Build();
    var config = Config.CreateAndValidate(configuration);

    Assert.That(config.FunctionName, Is.EqualTo("arn:aws:lambda:us-west-2:123456789012:function:my-function:qualifier"));
    Assert.That(config.FunctionNameOnly, Is.EqualTo("arn:aws:lambda:us-west-2:123456789012:function:my-function"));
    Assert.That(config.FunctionNameQualifier, Is.EqualTo("qualifier"));
  }

  [Test]
  public void TestCreateAndValidate_InvalidFunctionName_ThrowsException()
  {
    var inMemorySettings = new Dictionary<string, string> {
        {"FunctionName", "invalid function name"},
    };
    IConfiguration configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(inMemorySettings)
        .Build();

    Assert.Throws<ApplicationException>(() => Config.CreateAndValidate(configuration));
  }
}