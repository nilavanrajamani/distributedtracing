using Azure.Monitor.OpenTelemetry.AspNetCore;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// not needed, W3C is now default
// System.Diagnostics.Activity.DefaultIdFormat = System.Diagnostics.ActivityIdFormat.W3C;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddLogging(builder => builder.AddSeq());

builder.Services.AddRazorPages();

builder.Services.AddHttpClient();

builder.Services.AddOpenTelemetry().UseAzureMonitor(options => {
    options.ConnectionString = "InstrumentationKey=e1c310a6-654d-4265-9cc2-c30f9bd00311;IngestionEndpoint=https://southeastasia-1.in.applicationinsights.azure.com/;LiveEndpoint=https://southeastasia.livediagnostics.monitor.azure.com/";
});

//AddOpenTelemetryTracing(builder =>
builder.Services.AddOpenTelemetry().WithTracing(builder =>
{
    builder
        .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("WebClient"))
        .AddAspNetCoreInstrumentation(
            // if we wanted to ignore some specific requests, we could use the filter
            options => options.Filter = httpContext => !httpContext.Request.Path.Value?.Contains("/_framework/aspnetcore-browser-refresh.js") ?? true)
        .AddHttpClientInstrumentation(
            // we can hook into existing activities and customize them
            options => options.EnrichWithHttpRequestMessage = (activity, httpRequest) =>
            {
                activity.SetTag("requestProtocol", httpRequest.Method);
            }      
        )
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
        })
        ;
});

var app = builder.Build();

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseEndpoints(endpoints =>
{
    endpoints.MapRazorPages();
});

app.Run();