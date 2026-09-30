using System.Net;
using Kai.Engine.Agents;
using Kai.Engine.Configuration;
using Kai.Engine.Infrastructure;
using Kai.Engine.Ingest;
using Kai.Engine.Pipeline;
using Kai.Engine.Rpc;
using Kai.Engine.Scheduling;
using Kai.Engine.Storage;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

var engineSection = builder.Configuration.GetSection(EngineOptions.SectionName);
builder.Services.AddOptions<EngineOptions>().Bind(engineSection).ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<EngineOptions>, EngineOptionsValidator>();
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(90));

// One port only: gRPC over cleartext HTTP/2 on loopback. TLS is terminated by the reverse SSL/SSH tunnel.
builder.WebHost.ConfigureKestrel((context, kestrel) =>
{
    var engine = context.Configuration.GetSection(EngineOptions.SectionName).Get<EngineOptions>() ?? new EngineOptions();
    var address = IPAddress.TryParse(engine.ListenAddress, out var parsed) ? parsed : IPAddress.Loopback;
    kestrel.Listen(address, engine.Port, listen => listen.Protocols = HttpProtocols.Http2);
    kestrel.Limits.MaxRequestBodySize = null;
    kestrel.AddServerHeader = false;
});

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<RepositoryLayout>();
builder.Services.AddSingleton<EngineInstance>();
builder.Services.AddSingleton<EngineDatabase>();
builder.Services.AddSingleton<JobStore>();
builder.Services.AddSingleton<PipelineJobReader>();
builder.Services.AddSingleton<IPipelineScriptRunner, PowerShellPipelineScriptRunner>();
builder.Services.AddSingleton<FileIngestService>();
builder.Services.AddSingleton<JobStatusService>();
builder.Services.AddSingleton<IAgentSessionFactory, CopilotAgentSessionFactory>();
builder.Services.AddSingleton<AgentJobRunner>();
builder.Services.AddSingleton<SchedulerTrigger>();
builder.Services.AddSingleton<EngineBootstrapper>();
builder.Services.AddSingleton<JobSchedulerService>();

// Order matters: bootstrap (lock, schema, recovery) completes before the scheduler and the RPC port start.
builder.Services.AddHostedService<EngineStartupService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<JobSchedulerService>());

builder.Services.AddSingleton<EngineKeyInterceptor>();
builder.Services.AddGrpc(options =>
{
    options.Interceptors.Add<EngineKeyInterceptor>();
    options.MaxReceiveMessageSize = 4 * 1024 * 1024;
    options.EnableDetailedErrors = false;
});

var app = builder.Build();
app.MapGrpcService<TranslationEngineRpcService>();
app.Run();

public partial class Program;
