using core.domain.Interfaces.Services;

namespace core.IoC
{
    /// <summary>
    /// Expira as conversas da ponte cujo prazo passou (docs/specs/ponte-talento-consultor.md, D-PT6): 7 dias para o consultor responder e 7 para o talento confirmar.
    /// Roda a cada <see cref="Interval"/>; uma falha não derruba o host (o core inteiro cairia por causa de um job).
    /// </summary>
    public class ConversationExpirationJob : BackgroundService
    {
        public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

        private readonly IServiceScopeFactory _scopes;
        private readonly ILogger<ConversationExpirationJob> _logger;

        public ConversationExpirationJob(IServiceScopeFactory scopes, ILogger<ConversationExpirationJob> logger)
        {
            _scopes = scopes;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var scope = _scopes.CreateAsyncScope();
                    var expired = await scope.ServiceProvider.GetRequiredService<IConversationService>().ExpireDueAsync();
                    if (expired > 0)
                        _logger.LogInformation("{Count} conversas da ponte expiraram por falta de resposta.", expired);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Falha ao expirar as conversas da ponte; nova tentativa em {Minutes} min.", Interval.TotalMinutes);
                }

                try { await Task.Delay(Interval, stoppingToken); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
}
