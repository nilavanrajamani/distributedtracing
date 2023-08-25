using EasyNetQ;
using EasyNetQ.Topology;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Server;
using Newtonsoft.Json;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using System.Diagnostics;
using System.Text;

namespace IOT;

public class CtoDIotMessageHandler : IHostedService
{
    private static readonly ActivitySource ActivitySource = new ActivitySource(nameof(CtoDIotMessageHandler));
    private static readonly TextMapPropagator Propagator = new TraceContextPropagator();

    private readonly IBus _bus;
    private readonly ILogger<CtoDIotMessageHandler> _logger;
    private readonly Lazy<Exchange> _exchangectod;
    private readonly IMqttClient _mqttClient;
    private readonly MqttClientOptions mqttClientOptions;
    public const string ctodIncomingQueueWorker = "worker.ctod.gatewaytoiot";
    public const string ctodIncomingExchange = "exchange.ctod.gatewaytoiot";
    public const string ctodIncomingRoutingkey = "message.ctod.gatewaytoiot";
    public const string ctodOutgoingExchange = "exchange.ctod.iottodevice";
    public const string ctodOutgoingRoutingKey = "message.ctod.iottodevice";
    public CtoDIotMessageHandler(IBus bus, ILogger<CtoDIotMessageHandler> logger, IMqttClient mqttClient)
    {
        _bus = bus;
        _logger = logger;
        _exchangectod = new Lazy<Exchange>(() => _bus.Advanced.ExchangeDeclare(ctodOutgoingExchange, ExchangeType.Topic));
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

    private async Task HandleApplicationMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs arg)
    {
        await Task.CompletedTask;
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

    private async Task HandleConnectedAsync(MqttClientConnectedEventArgs arg)
    {
        await Task.CompletedTask;
    }


    public static TextMapPropagator Propagator1 => Propagator;

    #region NoChange
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var queue = GetQueue();

        using var subscription = _bus.Advanced.Consume(
            queue,
            async (messageBytes, properties, receivedInfo) =>
            {
                // Extract the PropagationContext of the upstream parent from the message headers
                var parentContext = Propagator1.Extract(default, properties, ExtractTraceContext);

                // Inject extracted info into current context
                Baggage.Current = parentContext.Baggage;

                // start an activity
                using var activity = ActivitySource.StartActivity("message receive", ActivityKind.Consumer, parentContext.ActivityContext, tags: new[] { new KeyValuePair<string, object?>("server", Environment.MachineName) });

                AddMessagingTags(activity, receivedInfo);

                var helloMessage = System.Text.Json.JsonSerializer.Deserialize<RequestPayload>(messageBytes.Span);

                _logger.LogInformation("Handling message: {message}", System.Text.Json.JsonSerializer.Deserialize<RequestPayload>(messageBytes.Span));

                await PublicMqttAsync(helloMessage);

            });

        await UntilCancelled(cancellationToken);

        Queue GetQueue()
        {
            var queue = _bus.Advanced.QueueDeclare(ctodIncomingQueueWorker);
            var exchange = _bus.Advanced.ExchangeDeclare(ctodIncomingExchange, ExchangeType.Topic);
            var binding = _bus.Advanced.Bind(exchange, queue, ctodIncomingRoutingkey);
            return queue;
        }

        IEnumerable<string> ExtractTraceContext(MessageProperties properties, string key)
        {
            try
            {
                if (properties.Headers.TryGetValue(key, out var value) && value is byte[] bytes)
                {
                    return new[] { Encoding.UTF8.GetString(bytes) };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to extract trace context");
            }

            return Enumerable.Empty<string>();
        }

        static void AddMessagingTags(Activity? activity, MessageReceivedInfo receivedInfo)
        {
            // https://github.com/open-telemetry/opentelemetry-dotnet/tree/core-1.1.0/examples/MicroserviceExample/Utils/Messaging
            // Following OpenTelemetry messaging specification conventions
            // See:
            //   * https://github.com/open-telemetry/opentelemetry-specification/blob/main/specification/trace/semantic_conventions/messaging.md#messaging-attributes
            //   * https://github.com/open-telemetry/opentelemetry-specification/blob/main/specification/trace/semantic_conventions/messaging.md#rabbitmq

            activity?.SetTag("messaging.system", "rabbitmq");
            activity?.SetTag("messaging.destination_kind", "queue");
            activity?.SetTag("messaging.destination", receivedInfo.Exchange);
            activity?.SetTag("messaging.rabbitmq.routing_key", receivedInfo.RoutingKey);
        }

        static async Task UntilCancelled(CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<bool>();
            using var ctRegistration = ct.Register(() => tcs.SetResult(true));
            await tcs.Task;
        }

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

    #endregion  

    public async Task PublishAsync<T>(T message)
    {
        using var activity = ActivitySource.StartActivity("message send", ActivityKind.Producer);
        var messageProperties = new MessageProperties();

        ActivityContext contextToInject = activity?.Context ?? Activity.Current?.Context ?? default;

        // Inject the ActivityContext into the message headers to propagate trace context to the receiving service.
        Propagator.Inject(new PropagationContext(contextToInject, Baggage.Current), messageProperties, InjectTraceContext);

        await _bus.Advanced.PublishAsync(_exchangectod.Value, ctodOutgoingRoutingKey, false, new Message<T>(message, messageProperties));

        void InjectTraceContext(MessageProperties messageProperties, string key, string value)
        {
            if (messageProperties.Headers is null)
            {
                messageProperties.Headers = new Dictionary<string, object>();
            }

            messageProperties.Headers[key] = value;
        }
    }

    public async Task PublicMqttAsync(RequestPayload? message)
    {

        using var activity = ActivitySource.StartActivity("message send", ActivityKind.Producer);
        var messageProperties = new MessagePropertiesMQTT();

        ActivityContext contextToInject = activity?.Context ?? Activity.Current?.Context ?? default;

        // Inject the ActivityContext into the message headers to propagate trace context to the receiving service.
        Propagator.Inject(new PropagationContext(contextToInject, Baggage.Current), messageProperties, InjectTraceContext);

        MQTTPayloadRequest mQTTPayload = new MQTTPayloadRequest() { Body = message, HeadersCollection = messageProperties };

        var applicationMessage = new MqttApplicationMessageBuilder()
            .WithTopic(ctodOutgoingExchange)
            .WithPayload(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(mQTTPayload)))
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();
        try
        {
            if (!_mqttClient.IsConnected)
            {
                await _mqttClient.ConnectAsync(mqttClientOptions);
            }


            await _mqttClient.PublishAsync(applicationMessage, CancellationToken.None);
        }
        catch (Exception ex)
        {

        }

        Console.WriteLine("MQTT application message is published.");


        void InjectTraceContext(MessagePropertiesMQTT messageProperties, string key, string value)
        {
            if (messageProperties.Headers is null)
            {
                messageProperties.Headers = new Dictionary<string, object>();
            }

            messageProperties.Headers[key] = value;
        }
    }
}


public class MQTTPayloadRequest
{
    public MessagePropertiesMQTT HeadersCollection { get; set; }
    public RequestPayload Body { get; set; }
}

public class MQTTPayloadResponse
{
    public MessagePropertiesMQTT HeadersCollection { get; set; }
    public ResponsePayload Body { get; set; }
}

public class MessagePropertiesMQTT
{
    public Dictionary<string, object> Headers { get; set; }
}