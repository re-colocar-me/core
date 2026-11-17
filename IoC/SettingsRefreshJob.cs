using core.domain.Interfaces.Services;

namespace core.IoC
{
    /// <summary>
    /// Mantém a configuração da ponte em dia com o que a equipe ajustou no backoffice (docs/specs/ponte-talento-consultor.md, seção 39). <b>Relê o banco ao subir</b> (antes de o processo atender
    /// qualquer pedido, para não rodar com os padrões por engano) <b>e a cada minuto</b>, para que uma mudança feita por outro processo do <c>core</c> também passe a valer aqui. Uma falha na leitura
    /// <b>não derruba nada</b>: a configuração em vigor continua a última que deu certo (ou os padrões do código, se nunca deu).
    /// </summary>
    public class SettingsRefreshJob : BackgroundService
    {
        public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

        private readonly IServiceScopeFactory _scopes;
        private readonly ILogger<SettingsRefreshJob> _logger;

        public SettingsRefreshJob(IServiceScopeFactory scopes, ILogger<SettingsRefreshJob> logger)
        {
            _scopes = scopes;
            _logger = logger;
        }

        /// <summary>A primeira leitura acontece <b>aqui</b>, antes de os outros serviços de segundo plano e do servidor começarem a trabalhar.</summary>
        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            await RefreshAsync(cancellationToken);
            await base.StartAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await Task.Delay(Interval, stoppingToken); }
                catch (OperationCanceledException) { return; }
                await RefreshAsync(stoppingToken);
            }
        }

        /// <summary>Uma leitura. Devolve se deu certo.</summary>
        public async Task<bool> RefreshAsync(CancellationToken ct)
        {
            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ISettingsService>().RefreshAsync();
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Não foi possível reler os parâmetros da ponte; segue valendo a última configuração lida (ou os padrões do código).");
                return false;
            }
        }
    }
}
