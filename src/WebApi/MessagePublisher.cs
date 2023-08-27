using EasyNetQ;
using EasyNetQ.Topology;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using System.Diagnostics;

namespace WebApi;

public class MessagePublisher : IDisposable
{
    private static readonly ActivitySource ActivitySource = new ActivitySource(nameof(MessagePublisher));
    private static readonly TextMapPropagator Propagator = Propagators.DefaultTextMapPropagator;

    private readonly IBus _bus;
    private readonly Lazy<Exchange> _exchange;

    public const string ctodExchange = "exchange.ctod.apptogateway";
    public const string ctodRoutingkey = "message.ctod.apptogateway";    


    public MessagePublisher(IBus bus)
    {
        _bus = bus;
        _exchange = new Lazy<Exchange>(() => _bus.Advanced.ExchangeDeclare(ctodExchange, ExchangeType.Topic));
    }

    public void Dispose() => _bus.Dispose();

    public async Task PublishAsync<T>(T message)
    {
        using var activity = ActivitySource.StartActivity("message send", ActivityKind.Producer);
        var messageProperties = new MessageProperties();

        ActivityContext contextToInject = activity?.Context ?? Activity.Current?.Context ?? default;

        // Inject the ActivityContext into the message headers to propagate trace context to the receiving service.
        Propagator.Inject(new PropagationContext(contextToInject, Baggage.Current), messageProperties, InjectTraceContext);

        await Task.Delay(TimeSpan.FromMilliseconds(20));

        await _bus.Advanced.PublishAsync(_exchange.Value, ctodRoutingkey, false, new Message<T>(message, messageProperties));

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