namespace lambda_dispatch.router;

public class Program
{
    public static void Main(string[] args)
    {
        Task.WaitAll(
            CreateHostBuilder(args, urls: "http://localhost:5000").Build().RunAsync(), // Public interface
            CreateHostBuilder(args, urls: "http://localhost:5001").Build().RunAsync()  // Control interface
        );
    }

    public static IHostBuilder CreateHostBuilder(string[] args, string urls) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseUrls(urls);
                webBuilder.UseStartup<Startup>();
            });
}