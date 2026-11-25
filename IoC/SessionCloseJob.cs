using core.domain.Interfaces.Infrastructure;
using core.domain.Interfaces.Services;
using core.domain.Services.Sessions;
using core.infrastructure.Repositories;
using Microsoft.Extensions.Options;

namespace core.IoC
{
    /// <summary>A chave e o ritmo da sessão em vídeo neste processo (seção <c>Sessions</c>). Os parâmetros das regras (janela, tolerâncias) são lidos da mesma seção por <c>SessionConfiguration</c>.</summary>
    public sealed class SessionHostOptions
    {
        public const string Section = "Sessions";

        /// <summary>Desligada por padrão: sem ela o gRPC responde 501 e o job não roda (nada lê as tabelas, que podem ainda não existir).</summary>
        public bool Enabled { get; set; }

        /// <summary>De quantos em quantos segundos o job fecha as sessões cuja janela acabou. Piso de 10 s.</summary>
        public int CloseEverySeconds { get; set; } = 60;
    }

    public static class SessionRegistration
    {
        public static IServiceCollection AddVideoSessions(this IServiceCollection services, IConfiguration configuration)
        {
            var section = configuration.GetSection(SessionHostOptions.Section);
            services.Configure<SessionHostOptions>(section);
            services.AddSingleton(section.Get<SessionConfiguration>() ?? new SessionConfiguration());
            services.AddTransient<ISessionRepository, SessionRepository>();
            services.AddTransient<ISessionService, SessionService>();
            services.AddHostedService<SessionCloseJob>();
            return services;
        }
    }

    /// <summary>
    /// Fecha as sessões cuja janela acabou e grava o resultado (docs/specs/sessao-em-video-e-chat.md, seção 5). Uma falha vira log e a rodada seguinte tenta de novo; nunca derruba o host.
    /// </summary>
    public class SessionCloseJob : BackgroundService
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly SessionHostOptions _options;
        private readonly ILogger<SessionCloseJob> _logger;

        public SessionCloseJob(IServiceScopeFactory scopes, IOptions<SessionHostOptions> options, ILogger<SessionCloseJob> logger)
        {
            _scopes = scopes;
            _options = options.Value;
            _logger = logger;
        }

        public TimeSpan Interval => TimeSpan.FromSeconds(Math.Max(10, _options.CloseEverySeconds));

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_options.Enabled) return;

            while (!stoppingToken.IsCancellationRequested)
            {
                await TickAsync(stoppingToken);
                try { await Task.Delay(Interval, stoppingToken); }
                catch (OperationCanceledException) { return; }
            }
        }

        public async Task TickAsync(CancellationToken ct)
        {
            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                var closed = await scope.ServiceProvider.GetRequiredService<ISessionService>().CloseDueAsync();
                if (closed > 0)
                    _logger.LogInformation("{Count} sessões em vídeo encerradas.", closed);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogError(ex, "Falha ao encerrar sessões em vídeo; nova tentativa em {Seconds} s.", Interval.TotalSeconds);
            }
        }
    }
}
