using EasyNetQ;
using EasyNetQ.Topology;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Server;
using Newtonsoft.Json;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using System;
using System.Diagnostics;
using System.Text;

namespace Device;

public class DeviceMessageHandler : IHostedService
{
    private static readonly ActivitySource ActivitySource = new ActivitySource(nameof(DeviceMessageHandler));
    private static readonly TextMapPropagator Propagator = new TraceContextPropagator();

    private readonly ILogger<DeviceMessageHandler> _logger;
    private readonly MqttClientOptions mqttClientOptions;
    private readonly IMqttClient _mqttClient;
    public const string ctodIncomingQueueWorker = "worker.ctod.iottodevice";
    public const string ctodIncomingExchange = "exchange.ctod.iottodevice";
    public const string ctodIncomingRoutingkey = "message.ctod.iottodevice";
    public const string dtocOutgoingExchange = "exchange.dtoc.devicetoiot";
    public const string dtocOutgoingRoutingKey = "message.dtoc.devicetoiot";

    public DeviceMessageHandler(ILogger<DeviceMessageHandler> logger, IMqttClient mqttClient)
    {
        _mqttClient = mqttClient;
        _logger = logger;
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
        MQTTPayloadRequest mQTTPayload = JsonConvert.DeserializeObject<MQTTPayloadRequest>(Encoding.UTF8.GetString(arg.ApplicationMessage.PayloadSegment));

        var parentContext = Propagator.Extract(default, mQTTPayload.HeadersCollection, ExtractTraceContextMQTT);

        // Inject extracted info into current context
        Baggage.Current = parentContext.Baggage;

        // start an activity
        using var activity = ActivitySource.StartActivity("message receive", ActivityKind.Consumer, parentContext.ActivityContext, tags: new[] { new KeyValuePair<string, object?>("server", Environment.MachineName) });

        AddMessagingTagsMQTT(activity);

        var helloMessage = mQTTPayload.Body;

        _logger.LogInformation("Handling message: {message}", helloMessage);

        await Task.Delay(TimeSpan.FromSeconds(2));

        var responsePayload = @$"Acknowledged message <b>{helloMessage?.message}</b> at {DateTime.Now}. This is received from the device";

        await PublicMqttAsync(new ResponsePayload(responsePayload));
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
        await _mqttClient.SubscribeAsync(ctodIncomingExchange);
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

    public async Task PublicMqttAsync(ResponsePayload? message)
    {        
        using var activity = ActivitySource.StartActivity("message send", ActivityKind.Producer);
        var messageProperties = new MessagePropertiesMQTT();

        ActivityContext contextToInject = activity?.Context ?? Activity.Current?.Context ?? default;

        // Inject the ActivityContext into the message headers to propagate trace context to the receiving service.
        Propagator.Inject(new PropagationContext(contextToInject, Baggage.Current), messageProperties, InjectTraceContext);

        MQTTPayloadResponse mQTTPayload = new MQTTPayloadResponse() { Body = message, HeadersCollection = messageProperties };

        var applicationMessage = new MqttApplicationMessageBuilder()
            .WithTopic(dtocOutgoingExchange)
            .WithPayload(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(mQTTPayload)))
            .Build();

        await _mqttClient.PublishAsync(applicationMessage, CancellationToken.None);

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

    #region NoChange
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

    #endregion

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