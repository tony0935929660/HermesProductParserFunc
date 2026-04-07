using HermesProductParserFunc.Functions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices(services =>
    {
        services.AddSingleton<HermesScraper>();
        services.AddHostedService<HermesWorker>();
    })
    .Build();

host.Run();
