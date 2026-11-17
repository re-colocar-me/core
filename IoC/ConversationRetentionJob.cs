using core.domain.Interfaces.Services;

namespace core.IoC
{
    /// <summary>
    /// A retenção dos dados da ponte talento-consultor (docs/specs/ponte-talento-consultor.md, seções 15.1 e 37), uma vez por dia: <b>anonimiza as conversas encerradas há mais de 90 dias</b> (a
    /// ficha, os eventos e os textos livres saem; a linha fica) e <b>apaga as denúncias já analisadas há mais de 365 dias</b>. A conversa aceita e a denúncia aberta nunca entram por prazo.
    /// Vem <b>ligada por padrão</b> (é obrigação de privacidade, e nada é apagado antes de 90 dias de uso); <c>ConversationRetention:Enabled=false</c> a desliga. Uma falha não derruba o host
    /// (o core inteiro cairia por causa de um job) e a rodada seguinte retoma de onde parou.
    /// </summary>
    public class ConversationRetentionJob : BackgroundService
    {
        public const string EnabledKey = "ConversationRetention:Enabled";
        public static readonly TimeSpan Interval = TimeSpan.FromHours(24);
        /// <summary>Depois de subir, espera um pouco: o core acaba de iniciar e a retenção não tem pressa.</summary>
        public static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(5);

        private readonly IServiceScopeFactory _scopes;
        private readonly IConfiguration _configuration;
        private readonly ILogger<ConversationRetentionJob> _logger;

        public ConversationRetentionJob(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<ConversationRetentionJob> logger)
        {
            _scopes = scopes;
            _configuration = configuration;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try { await Task.Delay(StartupDelay, stoppingToken); }
            catch (OperationCanceledException) { return; }

            while (!stoppingToken.IsCancellationRequested)
            {
                await TickAsync(stoppingToken);
                try { await Task.Delay(Interval, stoppingToken); }
                catch (OperationCanceledException) { return; }
            }
        }

        /// <summary>Uma rodada. Só um <c>false</c> explícito desliga (chave ausente ou valor inválido usam o padrão, que é ligado).</summary>
        public async Task<(int Conversations, int Reports)?> TickAsync(CancellationToken ct)
        {
            if (bool.TryParse(_configuration[EnabledKey], out var enabled) && !enabled)
            {
                _logger.LogInformation("A retenção dos dados da ponte está desligada ({Key}=false).", EnabledKey);
                return null;
            }

            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                var conversations = await scope.ServiceProvider.GetRequiredService<IConversationService>().ApplyRetentionAsync(ct);
                var reports = await scope.ServiceProvider.GetRequiredService<IModerationService>().ApplyRetentionAsync();
                if (conversations > 0 || reports > 0)
                    _logger.LogInformation("Retenção da ponte: {Conversations} conversas anonimizadas e {Reports} denúncias analisadas apagadas.", conversations, reports);
                return (conversations, reports);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha na retenção dos dados da ponte; nova tentativa em {Hours} h.", Interval.TotalHours);
                return null;
            }
        }
    }
}
