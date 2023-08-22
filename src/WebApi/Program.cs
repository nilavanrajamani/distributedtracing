using EasyNetQ;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using WebApi;
using Microsoft.AspNetCore.SignalR;
using System.Diagnostics;
using Microsoft.AspNetCore.Http;

// not needed, W3C is now default
// System.Diagnostics.Activity.DefaultIdFormat = System.Diagnostics.ActivityIdFormat.W3C;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSignalR();
builder.Services.AddLogging(builder => builder.AddSeq());
builder.Services.AddControllers();
builder.Services.AddSingleton<IBus>(_ => RabbitHutch.CreateBus("host=localhost"));

builder.Services.AddOpenTelemetryTracing(builder =>
{
    builder
        .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("WebApi"))
        .SetErrorStatusOnException(true)
         .AddSource(nameof(MessageHandler)) // when we manually create activities, we need to setup the sources here
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        // to avoid double activity, one for HttpClient, another for the gRPC client
        // -> https://github.com/open-telemetry/opentelemetry-dotnet/blob/core-1.1.0/src/OpenTelemetry.Instrumentation.GrpcNetClient/README.md#suppressdownstreaminstrumentation
        .AddGrpcClientInstrumentation(options => options.SuppressDownstreamInstrumentation = true)
        // besides instrumenting EF, we also want the queries to be part of the telemetry (hence SetDbStatementForText = true)
        .AddEntityFrameworkCoreInstrumentation(options => options.SetDbStatementForText = true)
        //.AddSource(nameof(MessagePublisher)) // when we manually create activities, we need to setup the sources here
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

builder.Services.AddSingleton<MessagePublisher>();
builder.Services.AddSingleton<ChatHub>();
builder.Services.AddHostedService<MessageHandler>();
builder.Services.AddCors(o => o.AddPolicy("CorsPolicy", builder =>
{
    builder.AllowAnyOrigin()
           .AllowAnyMethod()
           .AllowAnyHeader()
           .AllowCredentials()
           .WithOrigins("https://localhost:5001");
}));
var app = builder.Build();

app.UseHttpsRedirection();
app.UseRouting();
app.UseCors("CorsPolicy");

app.UseAuthorization();
app.MapHub<ChatHub>("/chatHub");

app.MapGet("/send", async (string payload, MessagePublisher messagePublisher) =>
{
    await messagePublisher.PublishAsync(new RequestPayload(payload));
    
    return new ResponsePayload($"Message Sent. Awaiting response from device. Trace id is {Activity.Current?.TraceId}");
});
//using (var scope = app.Services.CreateScope())
//{
//    var db = scope.ServiceProvider.GetRequiredService<HelloDbContext>();
//    db.Database.EnsureCreated(); // good only for demos 😉
//}

app.Run();

record RequestPayload(string message);
record ResponsePayload(string message);
//public record HelloResponse(string Greeting);

//public record HelloMessage(string Greeting);

//public record HelloEntry(Guid Id, string Username, DateTime CreatedAt);

//public class HelloDbContext : DbContext
//{

//#pragma warning disable CS8618 // DbSets populated by EF
//    public HelloDbContext(DbContextOptions<HelloDbContext> options) : base(options)
//    {
//    }


//    public DbSet<HelloEntry> HelloEntries { get; set; }
//#pragma warning restore CS8618

//    protected override void OnModelCreating(ModelBuilder modelBuilder)
//    {
//        modelBuilder.ApplyConfigurationsFromAssembly(GetType().Assembly);
//    }
//}

//public class HelloEntryConfiguration : IEntityTypeConfiguration<HelloEntry>
//{
//    public void Configure(EntityTypeBuilder<HelloEntry> builder)
//    {
//        builder.HasKey(e => e.Id);
//    }
//}