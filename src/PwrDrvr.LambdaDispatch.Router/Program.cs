namespace PwrDrvr.LambdaDispatch.Router;

public class Program
{
    public static void Main(string[] args)
    {
        Console.WriteLine($"Before ThreadPool ThreadCount: {ThreadPool.ThreadCount}");
        ThreadPool.SetMinThreads(20, 20);
        Console.WriteLine($"After ThreadPool ThreadCount: {ThreadPool.ThreadCount}");
        CreateHostBuilder(args).Build().Run();
    }

    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddAWSProvider();
                logging.AddSimpleConsole(options =>
                {
                    // options.IncludeScopes = true;
                    options.SingleLine = true;
                    options.TimestampFormat = "HH:mm:ss.fff ";
                });
#if DEBUG
                logging.SetMinimumLevel(LogLevel.Debug); // Set the minimum log level here
#else
                logging.SetMinimumLevel(LogLevel.Information); // Set the minimum log level here
#endif
            })
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseUrls("http://0.0.0.0:5002", "http://0.0.0.0:5001");
                webBuilder.UseStartup<Startup>();
            });
}