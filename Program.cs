using core.domain.Configurations;
using core.domain.Interfaces.Infrastructure;
using core.domain.Interfaces.Infrastructure.Repositories;
using core.domain.Interfaces.Services;
using core.domain.Services;
using core.infrastructure;
using core.infrastructure.ExternalServices.Facebook;
using core.infrastructure.ExternalServices.Linkedin;
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

// Add services to the container.

builder.Services.AddTransient<ILinkedinClient, LinkedinClient>();
builder.Services.AddTransient<IFacebookClient, FacebookClient>();

builder.Services.AddTransient<IProfileRepository, ProfileRepository>();
builder.Services.AddTransient<INotificationRepository, NotificationRepository>();
builder.Services.AddTransient<IConsultantServicesRepository, ConsultantServicesRepository>();
builder.Services.AddTransient<IScheduleRepository, ScheduleRepository>();

builder.Services.AddTransient<IConsultantServiceServices, ConsultantServiceServices>();
builder.Services.AddTransient<IConsultantServices, ConsultantService>();
builder.Services.AddTransient<IProfileService, ProfileServices>();
builder.Services.AddTransient<INotificationService, NotificationService>();
builder.Services.AddTransient<IScheduleService, ScheduleService>();

builder.Services.AddDbContext<ReColocarmeContext>((DbContextOptionsBuilder options) =>
{
    options.UseSqlServer(connectionString: configuration.GetConnectionString("Default") ?? string.Empty, dboptions =>
    {
        dboptions.EnableRetryOnFailure(10, TimeSpan.FromSeconds(1), null);
        dboptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
        dboptions.MigrationsAssembly(typeof(ReColocarmeContext).Assembly.GetName().Name);
    });
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
