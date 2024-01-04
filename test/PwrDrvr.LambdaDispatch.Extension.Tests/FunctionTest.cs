using NUnit.Framework;
using Amazon.Lambda.TestUtilities;
using PwrDrvr.LambdaDispatch.Messages;

namespace PwrDrvr.LambdaDispatch.Extension.Tests;

public class FunctionTest
{
    [Test]
    [Ignore("Does not work yet")]
    public async Task TestSampleFunction()
    {
        var function = new Function();
        var context = new TestLambdaContext();
        context.RemainingTime = TimeSpan.FromSeconds(10);
        var request = new WaiterRequest { Id = "1234", DispatcherUrl = "http://localhost:5003" };
        var response = await Function.FunctionHandler(request, context);

        Assert.Equals(request.Id, response.Id);
    }
}