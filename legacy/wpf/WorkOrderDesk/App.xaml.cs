using System;
using System.Configuration;
using System.Diagnostics;
using System.Windows;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace WorkOrderDesk
{
    public partial class App : Application
    {
        // The employee chosen at start-up; passed to every stored procedure as the actor.
        public static int ActorBusinessEntityID;
        public static string ActorName;

        // Decision B, step L7: the first change made to the legacy app is to retarget it to .NET Framework 4.8 so it can
        // emit a trace. Each stored-procedure call below gets a client span and hands its trace context to SQL Server.
        public static readonly ActivitySource Source = new ActivitySource("WorkOrderDesk");
        private TracerProvider _tracing;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            Activity.DefaultIdFormat = ActivityIdFormat.W3C;   // the trigger copies a W3C traceparent
            Activity.ForceDefaultIdFormat = true;
            var endpoint = ConfigurationManager.AppSettings["OtlpEndpoint"];
            if (string.IsNullOrEmpty(endpoint)) return;
            _tracing = Sdk.CreateTracerProviderBuilder()
                .ConfigureResource(r => r.AddService("workorder-desk-wpf-client"))
                .AddSource("WorkOrderDesk")
                .AddOtlpExporter(o => { o.Endpoint = new Uri(endpoint); o.Protocol = OtlpExportProtocol.HttpProtobuf; })
                .Build();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_tracing != null) { _tracing.ForceFlush(3000); _tracing.Dispose(); }
            base.OnExit(e);
        }
    }
}
