using EasyNetQ;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Device;
using MQTTnet.Client;
using MQTTnet;
using Azure.Monitor.OpenTelemetry.AspNetCore;

IHost host = Host.CreateDefaultBuilder(args)
    .ConfigureServices(services =>
    {
        services.AddSingleton<IMqttClient>(_ => (new MqttFactory()).CreateMqttClient());
        services.AddLogging(builder => builder.AddSeq());

        services.AddOpenTelemetry()
            .UseAzureMonitor(options => {
            options.ConnectionString = "InstrumentationKey=e1c310a6-654d-4265-9cc2-c30f9bd00311;IngestionEndpoint=https://southeastasia-1.in.applicationinsights.azure.com/;LiveEndpoint=https://southeastasia.livediagnostics.monitor.azure.com/";
        });

        services.AddOpenTelemetry().WithTracing(builder =>
        {
            builder
                .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("Device"))
                .SetErrorStatusOnException(true)
                .AddSource(nameof(DeviceMessageHandler)) // when we manually create activities, we need to setup the sources here
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
        services.AddHostedService<DeviceMessageHandler>();
    })
    .Build();

await host.RunAsync();

public record RequestPayload(string message);
public record ResponsePayload(string message);