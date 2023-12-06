namespace PwrDrvr.LambdaDispatch.Router;

public class Program
{
    public static void Main(string[] args)
    {
        CreateHostBuilder(args).Build().Run();
    }

    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddAWSProvider();
                logging.AddConsole();
                logging.SetMinimumLevel(LogLevel.Debug); // Set the minimum log level here
            })
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseUrls("http://0.0.0.0:5002", "http://0.0.0.0:5001");
                webBuilder.UseStartup<Startup>();
            });
}