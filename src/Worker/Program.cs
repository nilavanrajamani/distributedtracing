using EasyNetQ;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Gateway;

IHost host = Host.CreateDefaultBuilder(args)
    .ConfigureServices(services =>
    {
        services.AddSingleton<IBus>(_ => RabbitHutch.CreateBus("host=localhost;timeout=120"));
        services.AddLogging(builder => builder.AddSeq());
        services.AddOpenTelemetryTracing(builder =>
        {
            builder
                .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("Gateway"))
                .SetErrorStatusOnException(true)
                .AddSource(nameof(CtoDGatewayMessageHandler)) // when we manually create activities, we need to setup the sources here
                .AddSource(nameof(DtoCGatewayMessageHandler)) // when we manually create activities, we need to setup the sources here
                .AddZipkinExporter(options =>
                {
                    // not needed, it's the default
                    //options.Endpoint = new Uri("http://localhost:9411/api/v2/spans");
                })
                .AddJaegerExporter(options =>
                {
                    // not needed, it's the default
                    //options.AgentHost = "localhost";
                    //options.AgentPort = 6831;
                });
        });
        // OpenTelemetry adds an hosted service of its own, so we should register it before our hosted services,
        // or we might start handing messages (or whatever our background service does) before tracing is up and running
        // (noticed this as when I stopped this service and sent messages to the queue, the first message handled
        // wouldn't show up in the traces)
        services.AddHostedService<CtoDGatewayMessageHandler>();
        services.AddHostedService<DtoCGatewayMessageHandler>();
    })
    .Build();

await host.RunAsync();

record RequestPayload(string message);
record ResponsePayload(string message);