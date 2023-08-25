using EasyNetQ;
using EasyNetQ.Topology;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using System.Diagnostics;
using System.Text;

namespace Gateway;

public class CtoDGatewayMessageHandler : BackgroundService
{
    private static readonly ActivitySource ActivitySource = new ActivitySource(nameof(CtoDGatewayMessageHandler));
    private static readonly TextMapPropagator Propagator = new TraceContextPropagator();

    private readonly IBus _bus;
    private readonly ILogger<CtoDGatewayMessageHandler> _logger;
    private readonly Lazy<Exchange> _exchange;
    public const string ctodIncomingQueueWorker = "worker.ctod.apptogateway";
    public const string ctodIncomingExchange = "exchange.ctod.apptogateway";
    public const string ctodIncomingRoutingkey = "message.ctod.apptogateway";
    public const string ctodOutgoingExchange = "exchange.ctod.gatewaytoiot";
    public const string ctodOutgoingRoutingKey = "message.ctod.gatewaytoiot";

    public CtoDGatewayMessageHandler(IBus bus, ILogger<CtoDGatewayMessageHandler> logger)
    {
        _bus = bus;
        _logger = logger;
        _exchange = new Lazy<Exchange>(() => _bus.Advanced.ExchangeDeclare(ctodOutgoingExchange, ExchangeType.Topic));
    }

    public static TextMapPropagator Propagator1 => Propagator;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
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

                await PublishAsync(helloMessage);

            });

        await UntilCancelled(stoppingToken);

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
    }

    public async Task PublishAsync<T>(T message)
    {
        using var activity = ActivitySource.StartActivity("message send", ActivityKind.Producer);
        var messageProperties = new MessageProperties();

        ActivityContext contextToInject = activity?.Context ?? Activity.Current?.Context ?? default;

        // Inject the ActivityContext into the message headers to propagate trace context to the receiving service.
        Propagator.Inject(new PropagationContext(contextToInject, Baggage.Current), messageProperties, InjectTraceContext);

        await _bus.Advanced.PublishAsync(_exchange.Value, ctodOutgoingRoutingKey, false, new Message<T>(message, messageProperties));

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
