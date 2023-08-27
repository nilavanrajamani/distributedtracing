using EasyNetQ;
using EasyNetQ.Topology;
using MQTTnet.Client;
using MQTTnet.Server;
using Newtonsoft.Json;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry.Trace;
using System.Diagnostics;
using System.Text;

namespace IOT;

public class DtoCIotMessageHandler : IHostedService
{
    private static readonly ActivitySource ActivitySource = new ActivitySource(nameof(DtoCIotMessageHandler));
    private static readonly TextMapPropagator Propagator = new TraceContextPropagator();

    private readonly IBus _bus;
    private readonly ILogger<DtoCIotMessageHandler> _logger;
    private readonly IConfiguration _configuration;
    private readonly Lazy<Exchange> _exchangectod;
    private readonly IMqttClient _mqttClient;
    private readonly MqttClientOptions mqttClientOptions;
    public const string dtocIncomingQueueWorker = "worker.dtoc.devicetoiot";
    public const string dtocIncomingExchange = "exchange.dtoc.devicetoiot";
    public const string dtocIncomingRoutingkey = "message.dtoc.devicetoiot";
    public const string dtocOutgoingExchange = "exchange.dtoc.iottogateway";
    public const string dtocOutgoingRoutingKey = "message.dtoc.iottogateway";
    public DtoCIotMessageHandler(IBus bus, ILogger<DtoCIotMessageHandler> logger, IConfiguration configuration
        , IMqttClient mqttClient)
    {
        _bus = bus;
        _logger = logger;
        _configuration = configuration;
        _exchangectod = new Lazy<Exchange>(() => _bus.Advanced.ExchangeDeclare(dtocOutgoingExchange, ExchangeType.Topic));
        _mqttClient = mqttClient;
        mqttClientOptions = new MqttClientOptionsBuilder()
        .WithClientId(Guid.NewGuid().ToString())
                .WithTcpServer("localhost", 1883)
                .WithCleanSession()
                .Build();
        ConfigureMqttClient();
    }

    private void ConfigureMqttClient()
    {
        _mqttClient.ConnectedAsync += HandleConnectedAsync;
        _mqttClient.DisconnectedAsync += HandleDisconnectedAsync;
        _mqttClient.ApplicationMessageReceivedAsync += HandleApplicationMessageReceivedAsync;
    }

    private async Task HandleConnectedAsync(MqttClientConnectedEventArgs arg)
    {
        await _mqttClient.SubscribeAsync(dtocIncomingExchange);
    }

    private async Task HandleDisconnectedAsync(MqttClientDisconnectedEventArgs arg)
    {
        Console.WriteLine("Connection disconnected");
        #region Reconnect_Using_Event :https://github.com/dotnet/MQTTnet/blob/master/Samples/Client/Client_Connection_Samples.cs
        /*
        * This sample shows how to reconnect when the connection was dropped.
        * This approach uses one of the events from the client.
        * This approach has a risk of dead locks! Consider using the timer approach (see sample).
        * The following reconnection code "Reconnect_Using_Timer" is recommended
       */
        if (arg.ClientWasConnected)
        {
            // Use the current options as the new options.
            await _mqttClient.ConnectAsync(_mqttClient.Options);
        }
        #endregion
        await Task.CompletedTask;
    }

    private async Task HandleApplicationMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs arg)
    {
        MQTTPayloadResponse mQTTPayload = JsonConvert.DeserializeObject<MQTTPayloadResponse>(Encoding.UTF8.GetString(arg.ApplicationMessage.PayloadSegment));

        var parentContext = Propagator.Extract(default, mQTTPayload.HeadersCollection, ExtractTraceContextMQTT);

        // Inject extracted info into current context
        Baggage.Current = parentContext.Baggage;

        // start an activity
        using (var activity = ActivitySource.StartActivity("message receive", ActivityKind.Consumer, parentContext.ActivityContext, tags: new[] { new KeyValuePair<string, object?>("server", Environment.MachineName) }))
        {

            try
            {
                AddMessagingTagsMQTT(activity);

                if (_configuration.GetValue<bool>("SimulateException"))
                {
                    throw new Exception("Device to cloud message interrupted");
                }

                var helloMessage = mQTTPayload.Body;

                _logger.LogInformation("Handling message: {message}", helloMessage);

                await Task.Delay(TimeSpan.FromMilliseconds(20));

                await PublishAsync(helloMessage);
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.RecordException(ex);
                activity?.SetTag("messaging.destination_kind", "queue");
            }
        }
    }

    IEnumerable<string> ExtractTraceContextMQTT(MessagePropertiesMQTT properties, string key)
    {
        try
        {
            if (properties.Headers.TryGetValue(key, out var value) && value is string stringvalue)
            {
                return new[] { stringvalue };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract trace context");
        }

        return Enumerable.Empty<string>();
    }

    static void AddMessagingTagsMQTT(Activity? activity)
    {
        // https://github.com/open-telemetry/opentelemetry-dotnet/tree/core-1.1.0/examples/MicroserviceExample/Utils/Messaging
        // Following OpenTelemetry messaging specification conventions
        // See:
        //   * https://github.com/open-telemetry/opentelemetry-specification/blob/main/specification/trace/semantic_conventions/messaging.md#messaging-attributes
        //   * https://github.com/open-telemetry/opentelemetry-specification/blob/main/specification/trace/semantic_conventions/messaging.md#rabbitmq

        activity?.SetTag("messaging.system", "mqtt");
        activity?.SetTag("messaging.destination_kind", "queue");
    }

    public static TextMapPropagator Propagator1 => Propagator;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _mqttClient.ConnectAsync(mqttClientOptions);


        #region Reconnect_Using_Timer:https://github.com/dotnet/MQTTnet/blob/master/Samples/Client/Client_Connection_Samples.cs
        /* 
         * This sample shows how to reconnect when the connection was dropped.
         * This approach uses a custom Task/Thread which will monitor the connection status.
        * This is the recommended way but requires more custom code!
       */
        _ = Task.Run(
       async () =>
       {
           // // User proper cancellation and no while(true).
           while (true)
           {
               try
               {
                   // This code will also do the very first connect! So no call to _ConnectAsync_ is required in the first place.
                   if (!await _mqttClient.TryPingAsync())
                   {
                       await _mqttClient.ConnectAsync(_mqttClient.Options, CancellationToken.None);

                       // Subscribe to topics when session is clean etc.
                       Console.WriteLine("The MQTT client is connected.");
                   }
               }
               catch (Exception ex)
               {
                   // Handle the exception properly (logging etc.).
                   Console.WriteLine(ex);
               }
               finally
               {
                   // Check the connection state every 5 seconds and perform a reconnect if required.
                   await Task.Delay(TimeSpan.FromSeconds(5));
               }
           }

       });
        #endregion    
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            var disconnectOption = new MqttClientDisconnectOptions
            {
                Reason = MqttClientDisconnectOptionsReason.NormalDisconnection,
                ReasonString = "NormalDiconnection"
            };
            await _mqttClient.DisconnectAsync(disconnectOption, cancellationToken);
        }
        await _mqttClient.DisconnectAsync();
    }
    

    public async Task PublishAsync<T>(T message)
    {
        using var activity = ActivitySource.StartActivity("message send", ActivityKind.Producer);
        var messageProperties = new MessageProperties();

        ActivityContext contextToInject = activity?.Context ?? Activity.Current?.Context ?? default;

        // Inject the ActivityContext into the message headers to propagate trace context to the receiving service.
        Propagator.Inject(new PropagationContext(contextToInject, Baggage.Current), messageProperties, InjectTraceContext);

        await _bus.Advanced.PublishAsync(_exchangectod.Value, dtocOutgoingRoutingKey, false, new Message<T>(message, messageProperties));

        void InjectTraceContext(MessageProperties messageProperties, string key, string value)
        {
            if (messageProperties.Headers is null)
            {
                messageProperties.Headers = new Dictionary<string, object>();
            }

            messageProperties.Headers[key] = value;
        }
    }
}
