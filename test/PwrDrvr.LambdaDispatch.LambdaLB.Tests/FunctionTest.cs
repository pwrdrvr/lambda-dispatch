using Xunit;
using Amazon.Lambda.Core;
using Amazon.Lambda.TestUtilities;
using PwrDrvr.LambdaDispatch.Messages;

namespace PwrDrvr.LambdaDispatch.LambdaLB.Tests;

public class FunctionTest
{
    [Fact]
    public async void TestSampleFunction()
    {
        var function = new Function();
        var context = new TestLambdaContext();
        context.RemainingTime = TimeSpan.FromSeconds(10);
        var request = new WaiterRequest { Id = "1234", DispatcherUrl = "http://localhost:5001" };
        var response = await Function.FunctionHandler(request, context);

        Assert.Equal(request.Id, response.Id);
    }
}