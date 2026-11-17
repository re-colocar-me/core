using core.domain.Entities;
using core.domain.Interfaces.Services;
using core.domain.Services.Conversations;
using Google.Protobuf;

namespace core.Tests;

/// <summary>A moderação para a equipe (core/Services/ModerationGrpcService.cs): o que a fila mostra, o que NÃO mostra e como o erro vira código (docs/specs/ponte-talento-consultor.md, seção 29).</summary>
public class ModerationGrpcTests
{
    private static readonly Guid Consultant = Guid.Parse("cccccccc-1111-2222-3333-444444444444");
    private static readonly Guid Profile = Guid.Parse("dddddddd-1111-2222-3333-444444444444");

    private sealed class FakeModeration : IModerationService
    {
        public Exception? Throw { get; set; }
        public (string? Status, int Take)? Listed { get; private set; }
        public (Guid Id, string? Note)? Reviewed { get; private set; }
        public (Guid Consultant, string? Note)? Lifted { get; private set; }
        public Guid? Suspended { get; private set; }
        public Guid? Closed { get; private set; }

        public static ReportQueueItem Item() => new(Guid.NewGuid(), Guid.NewGuid(), new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc), "Open", "Candidate", "OutsideContact", "pediu zap", null, null,
            Consultant, "Marina Costa", "Invite", "Silenced", "Oi! — me chama fora", true, 5);

        public Task<ReportResult> ReportAsync(ReportCommand command) => throw new NotSupportedException();
        public Task<List<ReportQueueItem>> ListReportsAsync(string? status, int take)
        {
            if (Throw is not null) throw Throw;
            Listed = (status, take);
            return Task.FromResult(new List<ReportQueueItem> { Item() });
        }
        public Task<ReportQueueItem> ReviewReportAsync(Guid reportId, string? note)
        {
            if (Throw is not null) throw Throw;
            Reviewed = (reportId, note);
            return Task.FromResult(Item() with { ReportId = reportId, Status = "Reviewed", ReviewNote = note, ReviewedAt = new DateTime(2026, 11, 16, 13, 0, 0, DateTimeKind.Utc) });
        }
        public Task<List<SuspensionItem>> ListSuspensionsAsync() => Task.FromResult(new List<SuspensionItem> { new(Consultant, "Marina Costa", new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc), "AutoReports", 2, Profile, "Marina", "marina@exemplo.com"), new(Guid.NewGuid(), "Sem Contato", new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc), "Manual", 0) });
        public Task<int> ApplyRetentionAsync() => throw new NotSupportedException();
        public Task<int> CloseHeldInvitesAsync(Guid consultantId)
        {
            if (Throw is not null) throw Throw;
            Closed = consultantId;
            return Task.FromResult(2);
        }
        public Task<ConsultantInviteSuspension?> GetActiveSuspensionAsync(Guid consultantId) => Task.FromResult<ConsultantInviteSuspension?>(null);
        public Task<ConsultantInviteSuspension> SuspendAsync(Guid consultantId)
        {
            Suspended = consultantId;
            return Task.FromResult(new ConsultantInviteSuspension { Id = Guid.NewGuid(), ConsultantId = consultantId, SuspendedAt = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc), Reason = SuspensionReasons.Manual });
        }
        public Task LiftSuspensionAsync(Guid consultantId, string? note) { Lifted = (consultantId, note); return Task.CompletedTask; }
    }

    private static (core.Services.ModerationGrpcService Grpc, FakeModeration Service) Create()
    {
        var service = new FakeModeration();
        return (new core.Services.ModerationGrpcService(service), service);
    }

    [Fact]
    public async Task A_fila_repassa_o_filtro_e_o_limite_e_mapeia_tudo_que_a_equipe_le()
    {
        var (grpc, service) = Create();

        var reply = await grpc.ListReports(new ModListReportsRequest { Status = "Open", Take = 25 }, TestServerCallContext.Create());

        Assert.Equal(200, reply.Statuscode);
        Assert.Equal(("Open", 25), service.Listed);
        var item = Assert.Single(reply.Data.Unpack<ModListReportsReply>().Items);
        Assert.Equal("OutsideContact", item.Reason);
        Assert.Equal("pediu zap", item.Details);
        Assert.Equal("Marina Costa", item.ConsultantName);
        Assert.Equal("Oi! — me chama fora", item.ConversationText);
        Assert.True(item.ConsultantSuspended);
        Assert.Equal(5, item.DistinctInviteReporters30Days);
        Assert.Equal("2026-11-16T12:00:00.0000000Z", item.CreatedAt);
        Assert.Equal(string.Empty, item.ReviewedAt);            // ainda não analisada: texto vazio, não data zerada
    }

    [Fact]
    public void O_item_da_fila_nao_tem_nenhum_campo_que_identifique_o_talento()
    {
        var names = ModReportItem.Descriptor.Fields.InFieldNumberOrder().Select(f => f.Name.ToLowerInvariant()).ToList();

        Assert.DoesNotContain(names, n => n.Contains("candidate") || n.Contains("talent") || n.Contains("email") || n.Contains("phone") || n.Contains("picture"));
    }

    [Fact]
    public async Task Analisar_repassa_a_nota_e_devolve_a_denuncia_analisada()
    {
        var (grpc, service) = Create();
        var id = Guid.NewGuid();

        var reply = await grpc.ReviewReport(new ModReviewReportRequest { ReportId = id.ToString(), Note = "sem abuso" }, TestServerCallContext.Create());

        Assert.Equal((id, "sem abuso"), service.Reviewed);
        var item = reply.Data.Unpack<ModReportItem>();
        Assert.Equal("Reviewed", item.Status);
        Assert.Equal("sem abuso", item.ReviewNote);
        Assert.NotEqual(string.Empty, item.ReviewedAt);
    }

    [Fact]
    public async Task Suspender_levantar_e_encerrar_usam_o_consultant_id_e_respondem_o_que_a_equipe_precisa()
    {
        var (grpc, service) = Create();
        var ctx = TestServerCallContext.Create();

        var suspended = (await grpc.SuspendInviting(new ModConsultantRequest { ConsultantId = Consultant.ToString() }, ctx)).Data.Unpack<ModSuspensionReply>();
        await grpc.LiftInviting(new ModLiftRequest { ConsultantId = Consultant.ToString(), Note = "liberado" }, ctx);
        var closed = (await grpc.CloseHeldInvites(new ModConsultantRequest { ConsultantId = Consultant.ToString() }, ctx)).Data.Unpack<ModClosedReply>();
        var list = (await grpc.ListSuspensions(new ModEmptyRequest(), ctx)).Data.Unpack<ModListSuspensionsReply>();

        Assert.Equal(Consultant, service.Suspended);
        Assert.Equal("Manual", suspended.Reason);
        Assert.Equal((Consultant, "liberado"), service.Lifted);
        Assert.Equal(Consultant, service.Closed);
        Assert.Equal(2, closed.Closed);
        Assert.Equal(2, list.Items[0].HeldInvites);
        // O contato do consultor vai para o aviso a ele; quem não tem contato vai com os campos vazios (nunca nulos).
        Assert.Equal((Profile.ToString(), "Marina", "marina@exemplo.com"), (list.Items[0].ProfileId, list.Items[0].FirstName, list.Items[0].Email));
        Assert.Equal((string.Empty, string.Empty, string.Empty), (list.Items[1].ProfileId, list.Items[1].FirstName, list.Items[1].Email));
    }

    [Fact]
    public async Task Os_erros_viram_codigo()
    {
        var (grpc, service) = Create();
        var request = new ModReviewReportRequest { ReportId = Guid.NewGuid().ToString(), Note = "x" };

        service.Throw = new ReportNotFoundException();
        Assert.Equal(404, (await grpc.ReviewReport(request, TestServerCallContext.Create())).Statuscode);

        service.Throw = new ConversationRuleException("A nota pode ter até 400 caracteres.");
        var rule = await grpc.ReviewReport(request, TestServerCallContext.Create());
        Assert.Equal(400, rule.Statuscode);
        Assert.Equal("A nota pode ter até 400 caracteres.", rule.Error.Message);

        service.Throw = new ConversationRuleException("Este consultor não está com o direito de convidar suspenso.");
        Assert.Equal(400, (await grpc.CloseHeldInvites(new ModConsultantRequest { ConsultantId = Consultant.ToString() }, TestServerCallContext.Create())).Statuscode);

        Assert.Equal(400, (await grpc.ReviewReport(new ModReviewReportRequest { ReportId = "nao-e-guid" }, TestServerCallContext.Create())).Statuscode);
        Assert.Equal(400, (await grpc.SuspendInviting(new ModConsultantRequest { ConsultantId = Guid.Empty.ToString() }, TestServerCallContext.Create())).Statuscode);
    }
}
