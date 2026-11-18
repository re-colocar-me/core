using core.Auditing;
using core.domain.Auditing;
using core.domain.Configurations;
using core.domain.Entities;
using core.domain.Interfaces.Infrastructure;
using core.domain.Interfaces.Infrastructure.Repositories;
using core.domain.Interfaces.Services;
using core.domain.Services;
using core.domain.Services.Conversations;
using core.domain.Services.Match;
using core.infrastructure;
using core.infrastructure.Auditing;
using core.infrastructure.ExternalServices.Facebook;
using core.infrastructure.ExternalServices.Linkedin;
using core.infrastructure.ExternalServices.Hermes;
using core.infrastructure.ExternalServices.Elasticsearch;
using core.infrastructure.Repositiories;
using core.infrastructure.Repositories;
using core.Middleware;
using Microsoft.EntityFrameworkCore;
using Npgsql;
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
builder.Services.AddGrpc(options => options.Interceptors.Add<LoggingInterceptor>());

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
builder.Services.AddTransient<IReturnNoticeRepository, ReturnNoticeRepository>();
builder.Services.AddTransient<IReturnNoticeService, ReturnNoticeService>();
builder.Services.AddTransient<IScheduleRepository, ScheduleRepository>();
builder.Services.AddTransient<ITalentRepository, TalentRepository>();
builder.Services.AddTransient<ITutorialRepository, TutorialRepository>();
builder.Services.AddTransient<IJourneyRepository, JourneyRepository>();
builder.Services.AddTransient<ISwotAnalysisRepository, SwotAnalysisRepository>();
builder.Services.AddTransient<ISkillGapAnalysisRepository, SkillGapAnalysisRepository>();
builder.Services.AddTransient<ILinkedinSuggestionRepository, LinkedinSuggestionRepository>();
builder.Services.AddTransient<IAccountErasureRepository, AccountErasureRepository>();
builder.Services.AddTransient<IPersonalDocumentsEraser, PersonalDocumentsEraser>();
builder.Services.AddTransient<AccountErasureService>();

builder.Services.AddTransient<IConsultantServiceServices, ConsultantServiceServices>();
builder.Services.AddTransient<IConsultantServices, ConsultantService>();
builder.Services.AddTransient<ITalentServices, TalentServices>();
builder.Services.AddTransient<IProfileService, ProfileServices>();
builder.Services.AddTransient<INotificationService, NotificationService>();
builder.Services.AddTransient<IScheduleService, ScheduleService>();
builder.Services.AddTransient<IResumeSuggestionService, ResumeSuggestionService>();
builder.Services.AddTransient<ITutorialService, TutorialService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddTransient<IJourneyService, JourneyService>();
builder.Services.AddTransient<IJourneyRecorder, JourneyRecorder>();
builder.Services.AddTransient<IReviewRepository, ReviewRepository>();
builder.Services.AddTransient<IReviewService, ReviewService>();
builder.Services.AddTransient<ILeadRepository, LeadRepository>();
builder.Services.AddTransient<ILeadService, LeadService>();
builder.Services.AddTransient<IMatchRepository, MatchRepository>();
builder.Services.AddSingleton(new MatchConfiguration());
builder.Services.AddTransient<IMatchService, MatchService>();

// A ponte talento-consultor (docs/specs/ponte-talento-consultor.md): conversas, aceite que cria a conexão, e o job que expira as sem resposta.
builder.Services.AddTransient<IConversationRepository, ConversationRepository>();
// Os parâmetros da ponte são ajustáveis no backoffice (seção 39 de ponte-talento-consultor.md): UMA instância guarda a configuração em vigor (os padrões do código até a primeira leitura do banco), e
// cada serviço, que é Transient, recebe a que está em vigor no momento em que é criado.
builder.Services.AddSingleton<core.domain.Interfaces.Services.ConversationConfigurationHolder>();
builder.Services.AddTransient(sp => sp.GetRequiredService<core.domain.Interfaces.Services.ConversationConfigurationHolder>().Current);
builder.Services.AddTransient<core.domain.Interfaces.Infrastructure.ISettingsRepository, core.infrastructure.Repositories.SettingsRepository>();
builder.Services.AddTransient<core.domain.Interfaces.Services.ISettingsService, core.domain.Services.Settings.SettingsService>();
// A medição da ponte para a equipe (seção 40): só leitura, agregada e sem identidade.
builder.Services.AddTransient<core.domain.Interfaces.Infrastructure.IPonteMetricsRepository, core.infrastructure.Repositories.PonteMetricsRepository>();
builder.Services.AddTransient<core.domain.Interfaces.Services.IPonteMetricsService, core.domain.Services.Metrics.PonteMetricsService>();
// O pedido de exclusão de conta do consultor (docs/specs/exclusao-de-conta-consultor.md, EC1): só o pedido, a carência e o que impede. A EXECUÇÃO (os apagadores) é a EC2 e a EC3.
builder.Services.AddTransient<core.domain.Interfaces.Infrastructure.IConsultantDeletionRepository, core.infrastructure.Repositories.ConsultantDeletionRepository>();
builder.Services.AddTransient<core.domain.Interfaces.Services.IConsultantDeletionService, core.domain.Services.Deletion.ConsultantDeletionService>();
// A EXECUÇÃO da exclusão (EC2): apaga os dados do consultor e, por último, conclui (anonimiza o perfil). Reconfere tudo aqui; quem chama (o consultant-bff, EC3) só orquestra.
builder.Services.AddTransient<core.domain.Interfaces.Infrastructure.IConsultantErasureRepository, core.infrastructure.Repositories.ConsultantErasureRepository>();
builder.Services.AddTransient<core.domain.Interfaces.Services.IConsultantErasureService, core.domain.Services.Deletion.ConsultantErasureService>();
// Registrado ANTES dos outros serviços de segundo plano: a primeira leitura roda no StartAsync, antes de eles começarem.
builder.Services.AddHostedService<core.IoC.SettingsRefreshJob>();
builder.Services.AddTransient<IConversationConnections, ConsultantServicesConversationConnections>();
builder.Services.AddTransient<IConversationService, ConversationService>();
builder.Services.AddTransient<IAudienceRepository, AudienceRepository>();
builder.Services.AddTransient<IAudienceService, AudienceService>();
builder.Services.AddTransient<IInviteRepository, InviteRepository>();
builder.Services.AddTransient<IInviteService, InviteService>();
builder.Services.AddTransient<IModerationRepository, ModerationRepository>();
builder.Services.AddTransient<IModerationService, ModerationService>();
// Um só publicador (uma só conexão com o RabbitMQ) para todos os avisos do core: o do convite e os das conversas.
builder.Services.AddSingleton<core.infrastructure.Publishers.RabbitNotificationPublisher>();
builder.Services.AddSingleton<IInviteNoticePublisher>(sp => sp.GetRequiredService<core.infrastructure.Publishers.RabbitNotificationPublisher>());
builder.Services.AddSingleton<INotificationPublisher>(sp => sp.GetRequiredService<core.infrastructure.Publishers.RabbitNotificationPublisher>());
builder.Services.AddTransient<IConversationNoticeService, ConversationNoticeService>();
builder.Services.AddTransient<IInviteNoticeService, InviteNoticeService>();
builder.Services.AddHostedService<core.IoC.ConversationExpirationJob>();
builder.Services.AddHostedService<core.IoC.InviteNoticeJob>();
builder.Services.AddHostedService<core.IoC.ConversationNoticeJob>();
builder.Services.AddHostedService<core.IoC.ConversationRetentionJob>();

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
// individual de abrir a conexão ainda respeita o "Timeout" da própria connection string, um
// valor que mora no segredo do Kubernetes (fora do git, fora de code review). Uma rotação de
// credenciais pode recriar esse segredo sem esse parâmetro, derrubando a política de retry
// inteira sem nenhuma mudança de código — já aconteceu uma vez. Forçado aqui via
// NpgsqlConnectionStringBuilder pra não depender de ninguém preservar isso manualmente no segredo.
var connectionStringBuilder = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("Default") ?? string.Empty);
connectionStringBuilder.Timeout = Math.Max(30, connectionStringBuilder.Timeout);

builder.Services.AddDbContext<ReColocarmeContext>((IServiceProvider serviceProvider, DbContextOptionsBuilder options) =>
{
    options.UseNpgsql(connectionString: connectionStringBuilder.ConnectionString, dboptions =>
    {
        // Resiliência a restart de pod/blip de rede no Postgres em cluster (não mais o
        // auto-pause do Azure SQL free-tier, que motivou o valor original de 30s).
        dboptions.EnableRetryOnFailure(10, TimeSpan.FromSeconds(30), null);
        dboptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
        dboptions.MigrationsAssembly(typeof(ReColocarmeContext).Assembly.GetName().Name);
    });
    options.AddInterceptors(serviceProvider.GetRequiredService<AuditSaveChangesInterceptor>());
});

builder.Services.RegisterMasstransit(configuration);
builder.Services.AddHostedService<core.IoC.Messaging.JourneyEventsConsumer>();

var app = builder.Build();

// Configure the HTTP request pipeline.
app.MapGrpcService<coreServices.AuthenticationService>();
app.MapGrpcService<coreServices.NotificationService>();
app.MapGrpcService<coreServices.ConsultantService>();
app.MapGrpcService<coreServices.TalentService>();
app.MapGrpcService<coreServices.AccountService>();
app.MapGrpcService<coreServices.ConsultantServicesService>();
app.MapGrpcService<coreServices.ProfileService>();
app.MapGrpcService<coreServices.TutorialService>();
app.MapGrpcService<coreServices.JourneyService>();
app.MapGrpcService<coreServices.ReviewsService>();
app.MapGrpcService<coreServices.MatchingService>();
app.MapGrpcService<coreServices.LeadsService>();
app.MapGrpcService<coreServices.ConversationsService>();
app.MapGrpcService<coreServices.ModerationGrpcService>();
app.MapGrpcService<coreServices.SettingsGrpcService>();
app.MapGrpcService<coreServices.PonteMetricsGrpcService>();
app.MapGrpcService<coreServices.ConsultantDeletionGrpcService>();

app.MapGet("/", () => "Communication with gRPC endpoints must be made through a gRPC client. To learn how to create a client, visit: https://go.microsoft.com/fwlink/?linkid=2086909");


app.Run();
