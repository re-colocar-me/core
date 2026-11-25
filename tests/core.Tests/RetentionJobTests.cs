using core.domain.Interfaces.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace core.Tests;

/// <summary>O job diário da retenção da ponte (docs/specs/ponte-talento-consultor.md, seção 37): ligado por padrão, só um "false" explícito desliga, e uma falha não derruba o host.</summary>
public class RetentionJobTests
{
    private sealed class FakeConversations : IConversationService
    {
        public int Calls { get; private set; }
        public int Anonymized { get; set; } = 3;
        public Exception? Throw { get; set; }
        public Task<int> ApplyRetentionAsync(CancellationToken ct = default)
        {
            Calls++;
            if (Throw is not null) throw Throw;
            return Task.FromResult(Anonymized);
        }
        // O resto do contrato não é usado por este job.
        public Task<core.domain.Entities.Conversation> RaiseHandAsync(RaiseHandCommand command) => throw new NotSupportedException();
        public Task<core.domain.Entities.Conversation> ProposeAsync(Guid consultantId, Guid conversationId, Guid offeringId) => throw new NotSupportedException();
        public Task<core.domain.Entities.Conversation> DeclineAsync(Guid consultantId, Guid conversationId) => throw new NotSupportedException();
        public Task<core.domain.Entities.Conversation> ProposeFirstConversationAsync(Guid consultantId, Guid conversationId) => throw new NotSupportedException();
        public Task<FirstConversationStatus> GetFirstConversationAsync(Guid consultantId) => throw new NotSupportedException();
        public Task<Dictionary<Guid, int>> GetFirstConversationBadgesAsync(IEnumerable<Guid> consultantIds) => throw new NotSupportedException();
        public Task EnsureCanBuyFirstConversationAsync(Guid candidateId, Guid consultantId) => throw new NotSupportedException();
        public Task<Guid?> ResolveConsultantAsync(Guid ownerProfileId, bool createIfMissing = false) => throw new NotSupportedException();
        public Task<FirstConversationStatus> SetFirstConversationAsync(Guid consultantId, bool enabled, int durationMinutes, int monthlyQuota) => throw new NotSupportedException();
        public Task<core.domain.Entities.Conversation> ConfirmAsync(Guid candidateId, Guid conversationId, CandidateIdentity identity) => throw new NotSupportedException();
        public Task<core.domain.Entities.Conversation> WithdrawAsync(Guid candidateId, Guid conversationId) => throw new NotSupportedException();
        public Task<Dictionary<Guid, core.domain.Interfaces.Infrastructure.ConversationConsultant>> GetConsultantsAsync(IEnumerable<Guid> ids) => throw new NotSupportedException();
        public Task<List<core.domain.Entities.Conversation>> ListForCandidateAsync(Guid candidateId) => throw new NotSupportedException();
        public Task<List<core.domain.Entities.Conversation>> ListForConsultantAsync(Guid consultantId, core.domain.Enums.ConversationOrigin? origin, core.domain.Enums.ConversationStatus? status) => throw new NotSupportedException();
        public Task<int> ExpireDueAsync() => throw new NotSupportedException();
    }

    private sealed class FakeModeration : IModerationService
    {
        public int Calls { get; private set; }
        public int Deleted { get; set; } = 2;
        public Task<int> ApplyRetentionAsync() { Calls++; return Task.FromResult(Deleted); }
        public Task<ReportResult> ReportAsync(ReportCommand command) => throw new NotSupportedException();
        public Task<List<ReportQueueItem>> ListReportsAsync(string? status, int take) => throw new NotSupportedException();
        public Task<ReportQueueItem> ReviewReportAsync(Guid reportId, string? note) => throw new NotSupportedException();
        public Task<List<SuspensionItem>> ListSuspensionsAsync() => throw new NotSupportedException();
        public Task<List<HeldDeletionItem>> ListHeldDeletionsAsync() => throw new NotSupportedException();
        public Task<int> CloseHeldInvitesAsync(Guid consultantId) => throw new NotSupportedException();
        public Task<core.domain.Entities.ConsultantInviteSuspension?> GetActiveSuspensionAsync(Guid consultantId) => throw new NotSupportedException();
        public Task<core.domain.Entities.ConsultantInviteSuspension> SuspendAsync(Guid consultantId) => throw new NotSupportedException();
        public Task LiftSuspensionAsync(Guid consultantId, string? note) => throw new NotSupportedException();
    }

    private static (core.IoC.ConversationRetentionJob Job, FakeConversations Conversations, FakeModeration Moderation) Job(string? enabled)
    {
        var conversations = new FakeConversations();
        var moderation = new FakeModeration();
        var provider = new ServiceCollection().AddSingleton<IConversationService>(conversations).AddSingleton<IModerationService>(moderation).BuildServiceProvider();
        var values = enabled is null ? new Dictionary<string, string?>() : new Dictionary<string, string?> { ["ConversationRetention:Enabled"] = enabled };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return (new core.IoC.ConversationRetentionJob(provider.GetRequiredService<IServiceScopeFactory>(), configuration, NullLogger<core.IoC.ConversationRetentionJob>.Instance), conversations, moderation);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("true")]
    [InlineData("")]
    [InlineData("talvez")]
    public async Task Ligado_por_padrao_roda_as_duas_retencoes_e_devolve_os_numeros(string? enabled)
    {
        var (job, conversations, moderation) = Job(enabled);

        var result = await job.TickAsync(default);

        Assert.Equal((3, 2), result);
        Assert.Equal(1, conversations.Calls);
        Assert.Equal(1, moderation.Calls);
    }

    [Fact]
    public async Task So_um_false_explicito_desliga_e_nada_e_chamado()
    {
        var (job, conversations, moderation) = Job("false");

        Assert.Null(await job.TickAsync(default));

        Assert.Equal(0, conversations.Calls);
        Assert.Equal(0, moderation.Calls);
    }

    [Fact]
    public async Task Falha_vira_log_nao_derruba_o_job_e_a_rodada_seguinte_recupera()
    {
        var (job, conversations, _) = Job(null);
        conversations.Throw = new IOException("banco fora do ar");

        Assert.Null(await job.TickAsync(default));              // não lança

        conversations.Throw = null;
        Assert.NotNull(await job.TickAsync(default));
    }

    [Fact]
    public async Task O_cancelamento_nao_vira_erro()
    {
        var (job, conversations, _) = Job(null);
        conversations.Throw = new OperationCanceledException();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Null(await job.TickAsync(cts.Token));
    }

    [Fact]
    public void Roda_uma_vez_por_dia_e_espera_5_minutos_depois_de_subir()
    {
        Assert.Equal(TimeSpan.FromHours(24), core.IoC.ConversationRetentionJob.Interval);
        Assert.Equal(TimeSpan.FromMinutes(5), core.IoC.ConversationRetentionJob.StartupDelay);
    }
}
