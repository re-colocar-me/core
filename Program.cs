using core.Auditing;
using core.domain.Auditing;
using core.domain.Configurations;
using core.domain.Entities;
using core.domain.Interfaces.Infrastructure;
using core.domain.Interfaces.Infrastructure.Repositories;
using core.domain.Interfaces.Services;
using core.domain.Services;
using core.infrastructure;
using core.infrastructure.Auditing;
using core.infrastructure.ExternalServices.Facebook;
using core.infrastructure.ExternalServices.Linkedin;
using core.infrastructure.ExternalServices.Claude;
using core.infrastructure.Repositiories;
using core.infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Events;
using Elastic.Ingest.Elasticsearch;
using Elastic.Ingest.Elasticsearch.DataStreams;
using Elastic.Serilog.Sinks;
using coreServices = core.Services;
using core.IoC.Messaging;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, loggerConfiguration) => loggerConfiguration
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore.Hosting", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore.Mvc", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore.Routing", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .WriteTo.Elasticsearch(new[] { new Uri(context.Configuration["Elasticsearch:Uri"] ?? "http://localhost:9200") }, opts =>
            {
                opts.DataStream = new DataStreamName("logs", "core", context.HostingEnvironment.EnvironmentName.ToLowerInvariant());
                opts.BootstrapMethod = BootstrapMethod.Failure;
            }));

// Add services to the container.
builder.Services.AddGrpc();

var environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

var configuration = new ConfigurationBuilder()
    .AddJsonFile($"appsettings.json", true, true)
    .AddJsonFile($"appsettings.{environmentName}.json", true, true)
    .AddEnvironmentVariables().Build();

builder.Services.AddOptions();

builder.Services.Configure<LinkedinConfigurations>(configuration.GetSection(LinkedinConfigurations.LinkedinOptions));
builder.Services.Configure<FacebookConfigurations>(configuration.GetSection(FacebookConfigurations.FacebookOptions));
builder.Services.Configure<JwtConfiguration>(configuration.GetSection(JwtConfiguration.JwtOptions));
builder.Services.Configure<ScheduleServiceConfiguration>(configuration.GetSection(ScheduleServiceConfiguration.ScheduleServiceOptions));
builder.Services.Configure<AnthropicConfigurations>(configuration.GetSection(AnthropicConfigurations.AnthropicOptions));

// Add services to the container.

builder.Services.AddTransient<ILinkedinClient, LinkedinClient>();
builder.Services.AddTransient<IFacebookClient, FacebookClient>();
builder.Services.AddTransient<IAnthropicClient, ClaudeSuggestionClient>();

builder.Services.AddTransient<IProfileRepository, ProfileRepository>();
builder.Services.AddTransient<INotificationRepository, NotificationRepository>();
builder.Services.AddTransient<IConsultantServicesRepository, ConsultantServicesRepository>();
builder.Services.AddTransient<IScheduleRepository, ScheduleRepository>();

builder.Services.AddTransient<IConsultantServiceServices, ConsultantServiceServices>();
builder.Services.AddTransient<IConsultantServices, ConsultantService>();
builder.Services.AddTransient<IProfileService, ProfileServices>();
builder.Services.AddTransient<INotificationService, NotificationService>();
builder.Services.AddTransient<IScheduleService, ScheduleService>();
builder.Services.AddTransient<IResumeSuggestionService, ResumeSuggestionService>();

// Activity log (audit) — separate from the app's own Serilog pipeline/data stream on
// purpose: business audit trail, not application logs. See docs/specs/activity-log-backoffice.md.
var auditSerilogLogger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Elasticsearch(new[] { new Uri(configuration["Elasticsearch:Uri"] ?? "http://localhost:9200") }, opts =>
    {
        opts.DataStream = new DataStreamName("audit", "core", environmentName?.ToLowerInvariant() ?? "development");
        opts.BootstrapMethod = BootstrapMethod.Failure;
    })
    .CreateLogger();

builder.Services.AddSingleton(new AuditLogger(auditSerilogLogger));
builder.Services.AddScoped<ICurrentActorAccessor, CurrentActorAccessor>();
builder.Services.AddScoped<IAuditSink, SerilogAuditSink>();
builder.Services.AddSingleton(new Dictionary<Type, string>
{
    [typeof(ServiceCategory)] = "Categorias e Serviços",
    [typeof(ConsultantServiceItem)] = "Categorias e Serviços"
});
builder.Services.AddScoped<AuditSaveChangesInterceptor>();

builder.Services.AddDbContext<ReColocarmeContext>((IServiceProvider serviceProvider, DbContextOptionsBuilder options) =>
{
    options.UseSqlServer(connectionString: configuration.GetConnectionString("Default") ?? string.Empty, dboptions =>
    {
        dboptions.EnableRetryOnFailure(10, TimeSpan.FromSeconds(1), null);
        dboptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
        dboptions.MigrationsAssembly(typeof(ReColocarmeContext).Assembly.GetName().Name);
    });
    options.AddInterceptors(serviceProvider.GetRequiredService<AuditSaveChangesInterceptor>());
});

builder.Services.RegisterMasstransit(configuration);

var app = builder.Build();

// Configure the HTTP request pipeline.
app.MapGrpcService<coreServices.AuthenticationService>();
app.MapGrpcService<coreServices.NotificationService>();
app.MapGrpcService<coreServices.ConsultantService>();
app.MapGrpcService<coreServices.TalentService>();
app.MapGrpcService<coreServices.ConsultantServicesService>();
app.MapGrpcService<coreServices.ProfileService>();

app.MapGet("/", () => "Communication with gRPC endpoints must be made through a gRPC client. To learn how to create a client, visit: https://go.microsoft.com/fwlink/?linkid=2086909");


app.Run();
