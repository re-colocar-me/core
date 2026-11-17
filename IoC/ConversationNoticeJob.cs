using core.domain.Interfaces.Services;

namespace core.IoC
{
    /// <summary>
    /// Avisa quem precisa saber do que aconteceu numa conversa da ponte (docs/specs/ponte-talento-consultor.md, seções 14 e 38): o consultor quando um talento levanta a mão, confirma ou
    /// aceita um convite, e quando a proposta expira; o talento quando recebe uma proposta e quando o pedido é encerrado. Roda a cada <see cref="Interval"/> e <b>só quando a chave
    /// <c>ConversationNotices:Enabled</c> é <c>true</c></b> (desligada por padrão): publicar o código não manda e-mail nenhum até alguém ligar de propósito, depois de os tipos estarem publicados no
    /// catálogo do motor. Valor que não é <c>true</c> ou <c>false</c> conta como desligado. Uma falha não derruba o host; o RabbitMQ fora do ar vira log e a rodada seguinte tenta de novo, sem duplicar
    /// (a chave de idempotência da mensagem é o evento).
    /// </summary>
    public class ConversationNoticeJob : BackgroundService
    {
        public const string EnabledKey = "ConversationNotices:Enabled";
        public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

        private readonly IServiceScopeFactory _scopes;
        private readonly IConfiguration _configuration;
        private readonly ILogger<ConversationNoticeJob> _logger;
        private bool _loggedDisabled;

        public ConversationNoticeJob(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<ConversationNoticeJob> logger)
        {
            _scopes = scopes;
            _configuration = configuration;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await TickAsync(stoppingToken);
                try { await Task.Delay(Interval, stoppingToken); }
                catch (OperationCanceledException) { return; }
            }
        }

        /// <summary>Uma rodada. Lê a chave a cada vez.</summary>
        public async Task<ConversationNoticeRun?> TickAsync(CancellationToken ct)
        {
            // Valor que não é verdadeiro ou falso (por exemplo, "nao" ou vazio) conta como desligado: uma configuração errada nunca derruba o host nem liga o envio.
            if (!(bool.TryParse(_configuration[EnabledKey], out var enabled) && enabled))
            {
                if (!_loggedDisabled)
                    _logger.LogInformation("O aviso das conversas da ponte está desligado ({Key}=false).", EnabledKey);
                _loggedDisabled = true;
                return null;
            }
            _loggedDisabled = false;

            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                var run = await scope.ServiceProvider.GetRequiredService<IConversationNoticeService>().NotifyPendingAsync(ct);
                if (run.Published > 0 || run.Skipped > 0)
                    _logger.LogInformation("Avisos das conversas: {Published} publicados e {Skipped} sem aviso (o estado já mudou ou o destinatário não tem cadastro).", run.Published, run.Skipped);
                if (run.Interrupted)
                    _logger.LogWarning("O aviso das conversas parou no meio: o RabbitMQ não guardou a mensagem. O resto sai na próxima rodada.");
                return run;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao avisar as conversas; nova tentativa em {Minutes} min.", Interval.TotalMinutes);
                return null;
            }
        }
    }
}
