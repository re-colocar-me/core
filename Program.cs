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
using core.infrastructure.ExternalServices.Hermes;
using core.infrastructure.ExternalServices.Elasticsearch;
using core.infrastructure.Repositiories;
using core.infrastructure.Repositories;
using Microsoft.Data.SqlClient;
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
                // Failure derrubava o processo inteiro (SIGABRT) se o Elasticsearch não estivesse
                // acessível no boot — visto de verdade no core-vocacional em produção. Silent deixa
                // o app subir normalmente e só perde log se o ES estiver fora.
                opts.BootstrapMethod = BootstrapMethod.Silent;
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
builder.Services.Configure<HermesAgentConfigurations>(configuration.GetSection(HermesAgentConfigurations.HermesAgentOptions));
builder.Services.Configure<TutorialConfiguration>(configuration.GetSection(TutorialConfiguration.TutorialOptions));

// Add services to the container.

builder.Services.AddTransient<ILinkedinClient, LinkedinClient>();
builder.Services.AddTransient<IFacebookClient, FacebookClient>();
builder.Services.AddTransient<IAiTextClient, HermesAgentClient>();

builder.Services.AddTransient<IProfileRepository, ProfileRepository>();
builder.Services.AddTransient<INotificationRepository, NotificationRepository>();
builder.Services.AddTransient<IConsultantServicesRepository, ConsultantServicesRepository>();
builder.Services.AddTransient<IScheduleRepository, ScheduleRepository>();
builder.Services.AddTransient<ITalentRepository, TalentRepository>();
builder.Services.AddTransient<ITutorialRepository, TutorialRepository>();
builder.Services.AddTransient<ISwotAnalysisRepository, SwotAnalysisRepository>();

builder.Services.AddTransient<IConsultantServiceServices, ConsultantServiceServices>();
builder.Services.AddTransient<IConsultantServices, ConsultantService>();
builder.Services.AddTransient<ITalentServices, TalentServices>();
builder.Services.AddTransient<IProfileService, ProfileServices>();
builder.Services.AddTransient<INotificationService, NotificationService>();
builder.Services.AddTransient<IScheduleService, ScheduleService>();
builder.Services.AddTransient<IResumeSuggestionService, ResumeSuggestionService>();
builder.Services.AddTransient<ITutorialService, TutorialService>();

// Activity log (audit) — separate from the app's own Serilog pipeline/data stream on
// purpose: business audit trail, not application logs. See docs/specs/activity-log-backoffice.md.
var auditSerilogLogger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Elasticsearch(new[] { new Uri(configuration["Elasticsearch:Uri"] ?? "http://localhost:9200") }, opts =>
    {
        opts.DataStream = new DataStreamName("audit", "core", environmentName?.ToLowerInvariant() ?? "development");
        opts.BootstrapMethod = BootstrapMethod.Silent;
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

// EnableRetryOnFailure abaixo só controla o intervalo ENTRE tentativas — cada tentativa
// individual de abrir a conexão ainda respeita o "Connection Timeout" da própria connection
// string, um valor que mora no segredo do Kubernetes (fora do git, fora de code review). Uma
// rotação de credenciais do SQL pode recriar esse segredo sem esse parâmetro, derrubando a
// política de retry inteira sem nenhuma mudança de código ("SqlException: Connection Timeout
// Expired" mesmo com EnableRetryOnFailure configurado) — já aconteceu uma vez. Forçado aqui via
// SqlConnectionStringBuilder pra não depender de ninguém preservar isso manualmente no segredo.
var connectionStringBuilder = new SqlConnectionStringBuilder(configuration.GetConnectionString("Default") ?? string.Empty);
connectionStringBuilder.ConnectTimeout = Math.Max(30, connectionStringBuilder.ConnectTimeout);

builder.Services.AddDbContext<ReColocarmeContext>((IServiceProvider serviceProvider, DbContextOptionsBuilder options) =>
{
    options.UseSqlServer(connectionString: connectionStringBuilder.ConnectionString, dboptions =>
    {
        // maxRetryDelay capped at 30s, not 1s — the shared free-tier Azure SQL database auto-pauses
        // when idle and takes up to ~30s to resume; a 1s cap meant EF gave up long before that.
        dboptions.EnableRetryOnFailure(10, TimeSpan.FromSeconds(30), null);
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
app.MapGrpcService<coreServices.TutorialService>();

app.MapGet("/", () => "Communication with gRPC endpoints must be made through a gRPC client. To learn how to create a client, visit: https://go.microsoft.com/fwlink/?linkid=2086909");


app.Run();
