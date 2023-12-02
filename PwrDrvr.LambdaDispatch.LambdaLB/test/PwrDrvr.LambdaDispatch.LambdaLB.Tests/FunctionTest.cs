using Xunit;
using Amazon.Lambda.Core;
using Amazon.Lambda.TestUtilities;
using PwrDrvr.LambdaDispatch.Messages;

namespace PwrDrvr.LambdaDispatch.LambdaLB.Tests;

public class FunctionTest
{
    [Fact]
    public void TestSampleFunction()
    {
        var function = new Function();
        var context = new TestLambdaContext();
        var request = new WaiterRequest("1234", "http://localhost:5001");
        var response = function.FunctionHandler(request, context);

        Assert.Equal(request.Id, response.Id);
    }
}