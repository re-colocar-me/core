using core.domain.Interfaces.Services;

namespace core.IoC
{
    /// <summary>
    /// Avisa os talentos dos convites recém-enviados (docs/specs/ponte-talento-consultor.md, seção 33). Roda a cada <see cref="Interval"/> e <b>só quando a chave
    /// <c>InviteNotices:Enabled</c> está ligada</b> (desligada por padrão): publicar o código não manda e-mail nenhum até alguém ligar de propósito, depois de o tipo estar publicado no
    /// catálogo do motor. Uma falha não derruba o host (o core inteiro cairia por causa de um job); o RabbitMQ fora do ar vira log e a rodada seguinte tenta de novo, sem duplicar
    /// (a chave de idempotência da mensagem é o convite).
    /// </summary>
    public class InviteNoticeJob : BackgroundService
    {
        public const string EnabledKey = "InviteNotices:Enabled";
        public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

        private readonly IServiceScopeFactory _scopes;
        private readonly IConfiguration _configuration;
        private readonly ILogger<InviteNoticeJob> _logger;
        private bool _loggedDisabled;

        public InviteNoticeJob(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<InviteNoticeJob> logger)
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

        /// <summary>Uma rodada. Lê a chave a cada vez (dá para ligar e desligar sem reiniciar, se a configuração for recarregada).</summary>
        public async Task<InviteNoticeRun?> TickAsync(CancellationToken ct)
        {
            // Valor que não é verdadeiro ou falso (por exemplo, "nao" ou vazio) conta como desligado: uma configuração errada nunca derruba o host nem liga o envio.
            if (!(bool.TryParse(_configuration[EnabledKey], out var enabled) && enabled))
            {
                if (!_loggedDisabled)
                    _logger.LogInformation("O aviso ao talento dos convites está desligado ({Key}=false).", EnabledKey);
                _loggedDisabled = true;
                return null;
            }
            _loggedDisabled = false;

            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                var run = await scope.ServiceProvider.GetRequiredService<IInviteNoticeService>().NotifyPendingAsync(ct);
                if (run.Published > 0 || run.SkippedByPreference > 0)
                    _logger.LogInformation("Avisos de convite: {Published} publicados e {Skipped} sem aviso (o talento desligou os convites).", run.Published, run.SkippedByPreference);
                if (run.Interrupted)
                    _logger.LogWarning("O aviso dos convites parou no meio: o RabbitMQ não guardou a mensagem. O resto sai na próxima rodada.");
                return run;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao avisar os convites; nova tentativa em {Minutes} min.", Interval.TotalMinutes);
                return null;
            }
        }
    }
}
