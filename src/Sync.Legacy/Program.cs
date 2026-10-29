using Lab.Observability;
using Sync.Contracts;
using Sync.Legacy;
using Sync.RabbitMq;

var builder = Host.CreateApplicationBuilder(args);
var uri = builder.Configuration["RabbitMq:Uri"] ?? throw new InvalidOperationException("RabbitMq:Uri is not set");
// Connect lazily and keep retrying, so the worker survives starting before the broker is up.
builder.Services.AddSingleton<IMessageBus>(_ =>
{
    while (true)
    {
        try { return RabbitMqBus.ConnectAsync(uri).GetAwaiter().GetResult(); }
        catch (Exception) { Thread.Sleep(2000); }
    }
});
builder.Services.AddLabTelemetry("workorder-desk-legacy-sync");
builder.Services.AddHostedService<BacklogSampler>();
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<CommandConsumer>();
builder.Build().Run();
