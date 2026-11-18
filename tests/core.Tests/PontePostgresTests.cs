using core.domain.Entities;
using core.domain.Enums;
using core.domain.Interfaces.Services;
using core.domain.Services.Metrics;
using core.infrastructure;
using core.infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace core.Tests;

// Dentro do namespace: `core.Profile` e `core.Consultant` (classes gRPC) venceriam um alias declarado fora dele.
using Profile = core.domain.Entities.Profile;
using Consultant = core.domain.Entities.Consultant;

/// <summary>
/// A PROVA em Postgres real do SQL da ponte que só tinha sido lido: a medição (seção 40 de <c>ponte-talento-consultor.md</c>), a consulta que o job de avisos das conversas usa (seção 38) e o repositório dos
/// parâmetros ajustáveis (seção 39). Só roda com <c>CORE_TEST_PG</c>; sem ela é pulado.
/// </summary>
public class PontePostgresTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedClock(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utc, TimeSpan.Zero);
    }

    private static async Task<(Func<ReColocarmeContext> Make, Func<Task> Drop)> CreateDatabaseAsync()
    {
        var admin = Environment.GetEnvironmentVariable(PostgresFactAttribute.Variable)!;
        var name = "core_ponte_" + Guid.NewGuid().ToString("N")[..10];

        await using (var connection = new NpgsqlConnection(admin + ";Database=postgres"))
        {
            await connection.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await create.ExecuteNonQueryAsync();
        }

        var cs = admin + ";Database=" + name;
        ReColocarmeContext Make() => new(new DbContextOptionsBuilder<ReColocarmeContext>().UseNpgsql(cs).Options);
        await using (var migrate = Make())
            await migrate.Database.MigrateAsync();

        async Task Drop()
        {
            NpgsqlConnection.ClearAllPools();
            await using var connection = new NpgsqlConnection(admin + ";Database=postgres");
            await connection.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", connection);
            await drop.ExecuteNonQueryAsync();
        }

        return (Make, Drop);
    }

    private sealed record Seeded(Guid ConsultantId, Guid RaisedId, Guid ConfirmedId);

    private static Conversation Convo(Guid consultantId, ConversationOrigin origin, ConversationStatus status, DateTime createdAt, ConversationProposalKind? proposal = null, params (string Type, string Actor, TimeSpan After)[] events)
        => new()
        {
            Id = Guid.NewGuid(), ConsultantId = consultantId, CandidateId = Guid.NewGuid(), Origin = origin, Status = status, CreatedAt = createdAt, UpdatedAt = createdAt, ProposalKind = proposal,
            Events = events.Select(e => new ConversationEvent { Id = Guid.NewGuid(), Type = e.Type, Actor = e.Actor, At = createdAt + e.After }).ToList()
        };

    private static async Task<Seeded> SeedAsync(ReColocarmeContext db)
    {
        var consultantProfile = Guid.NewGuid();
        var consultantId = Guid.NewGuid();
        db.Profiles.Add(new Profile { Id = consultantProfile, Email = "marina@exemplo.com", Name = new PersonName { FirstName = "Marina", LastName = "Costa" }, Role = RoleType.Consultant });
        db.Consultants.Add(new Consultant { Id = consultantId, ProfileId = consultantProfile, Name = new PersonName { FirstName = "Marina", LastName = "Costa" } });

        var t0 = Now.AddDays(-5);
        var raised = Convo(consultantId, ConversationOrigin.HandRaised, ConversationStatus.Raised, t0, null, ("Raised", "Candidate", TimeSpan.Zero));
        var confirmed = Convo(consultantId, ConversationOrigin.HandRaised, ConversationStatus.Confirmed, t0, ConversationProposalKind.Service,
            ("Raised", "Candidate", TimeSpan.Zero), ("Proposed", "Consultant", TimeSpan.FromMinutes(90)), ("Confirmed", "Candidate", TimeSpan.FromHours(5)));
        var declined = Convo(consultantId, ConversationOrigin.HandRaised, ConversationStatus.Declined, t0, null, ("Raised", "Candidate", TimeSpan.Zero), ("Declined", "Consultant", TimeSpan.FromMinutes(30)));
        var dismissed = Convo(consultantId, ConversationOrigin.Invite, ConversationStatus.Dismissed, t0, null, ("Sent", "Consultant", TimeSpan.Zero));
        var old = Convo(consultantId, ConversationOrigin.HandRaised, ConversationStatus.Raised, Now.AddDays(-80), null, ("Raised", "Candidate", TimeSpan.Zero));          // fora da janela de 30 dias
        db.Conversations.AddRange(raised, confirmed, declined, dismissed, old);
        db.Profiles.Add(new Profile { Id = confirmed.CandidateId, Email = "ana@exemplo.com", Name = new PersonName { FirstName = "Ana", LastName = "Lima" }, Role = RoleType.Talent });
        await db.SaveChangesAsync();

        db.ConversationReports.Add(new ConversationReport { Id = Guid.NewGuid(), ConversationId = dismissed.Id, ConsultantId = consultantId, CandidateId = dismissed.CandidateId, ReporterRole = ReporterRoles.Candidate, Origin = ConversationOrigin.Invite, Reason = ReportReason.Spam, CreatedAt = Now.AddDays(-2) });
        db.ConsultantInviteSuspensions.Add(new ConsultantInviteSuspension { Id = Guid.NewGuid(), ConsultantId = consultantId, SuspendedAt = Now.AddDays(-1), Reason = SuspensionReasons.AutoReports });
        await db.SaveChangesAsync();
        return new Seeded(consultantId, raised.Id, confirmed.Id);
    }

    [PostgresFact]
    public async Task A_medicao_roda_em_Postgres_real_funis_tempo_de_resposta_denuncias_e_suspensoes()
    {
        var (make, drop) = await CreateDatabaseAsync();
        try
        {
            await using (var db = make()) await SeedAsync(db);

            await using var read = make();
            var metrics = await new PonteMetricsService(new PonteMetricsRepository(read), new FixedClock(Now)).GetAsync(30);

            Assert.Equal(new HandRaiseFunnel(Raised: 3, WithProposal: 1, Confirmed: 1, Declined: 1, ExpiredNoResponse: 0, ExpiredAfterProposal: 0, Withdrawn: 0, Open: 1), metrics.HandRaise);   // a de 80 dias fica de fora
            Assert.Equal(new InviteFunnel(Sent: 1, Accepted: 0, Dismissed: 1, Silenced: 0, Expired: 0, Held: 0, Open: 0), metrics.Invites);
            Assert.Equal(new FirstConversationFunnel(0, 0), metrics.FirstConversation);

            // O tempo vem do PRIMEIRO evento Proposed ou Declined: 90 min e 30 min (a confirmação, 5 h depois, não conta; a sem resposta fica de fora).
            Assert.Equal(2, metrics.ResponseTime.Measured);
            Assert.Equal(30, metrics.ResponseTime.MedianMinutes);
            Assert.Equal(90, metrics.ResponseTime.P90Minutes);
            Assert.Equal(1, metrics.ResponseTime.Buckets.Single(b => b.Key == "lt1h").Count);
            Assert.Equal(1, metrics.ResponseTime.Buckets.Single(b => b.Key == "1to6h").Count);
            Assert.Equal(0, metrics.ResponseTime.NoData);

            Assert.Equal(1, metrics.Reports.InviteByTalent);
            Assert.Equal(1, metrics.Reports.Open);
            Assert.Equal(1, metrics.Reports.SuspensionsAuto);
            Assert.Equal(0, metrics.Reports.SuspensionsLifted);
        }
        finally { await drop(); }
    }

    [PostgresFact]
    public async Task A_consulta_dos_avisos_das_conversas_junta_os_cadastros_e_o_evento_marcado_deixa_de_voltar()
    {
        var (make, drop) = await CreateDatabaseAsync();
        try
        {
            Seeded seeded;
            await using (var db = make()) seeded = await SeedAsync(db);

            await using var read = make();
            var repository = new ConversationRepository(read);
            var pending = await repository.ListPendingEventNoticesAsync(Now.AddDays(-30), 50);

            // Eventos que viram aviso (Raised, Proposed, Confirmed, Declined, Expired) da janela; o "Sent" do convite não entra, nem o da conversa de 80 dias.
            Assert.Equal(new[] { "Declined", "Proposed", "Confirmed", "Raised", "Raised", "Raised" }.Order().ToArray(), pending.Select(p => p.EventType).Order().ToArray());
            var raised = pending.Single(p => p.ConversationId == seeded.RaisedId);
            Assert.Equal(("marina@exemplo.com", "Marina", "Marina Costa"), (raised.ConsultantEmail, raised.ConsultantFirstName, raised.ConsultantName));
            Assert.False(raised.HadProposal);

            var confirmed = pending.Single(p => p.ConversationId == seeded.ConfirmedId && p.EventType == "Confirmed");
            Assert.True(confirmed.HadProposal);                                                                          // a subconsulta EXISTS enxerga o evento Proposed
            Assert.Equal("ana@exemplo.com", confirmed.CandidateEmail);                                                   // a junção à esquerda traz o cadastro do talento
            Assert.Equal(pending.OrderBy(p => p.EventAt).First().EventAt, pending.Min(p => p.EventAt));                  // do mais antigo ao mais novo

            await repository.MarkEventNoticedAsync(raised.EventId, Now);
            await repository.MarkEventNoticedAsync(raised.EventId, Now.AddHours(1));                                     // marcar de novo não muda nada

            await using var after = make();
            var left = await new ConversationRepository(after).ListPendingEventNoticesAsync(Now.AddDays(-30), 50);
            Assert.DoesNotContain(left, p => p.EventId == raised.EventId);
            Assert.Equal(pending.Count - 1, left.Count);
            Assert.Equal(Now, (await after.ConversationEvents.SingleAsync(e => e.Id == raised.EventId)).NotifiedAt);      // a primeira marca vale
        }
        finally { await drop(); }
    }

    [PostgresFact]
    public async Task Os_parametros_gravam_trocam_voltam_ao_padrao_e_o_historico_vai_na_mesma_transacao()
    {
        var (make, drop) = await CreateDatabaseAsync();
        try
        {
            await using var db = make();
            var repository = new SettingsRepository(db);

            await repository.SaveAsync(new PlatformSetting { Key = "InviteMonthlyQuota", Value = 20, UpdatedAt = Now, UpdatedBy = "Ana" },
                new PlatformSettingChange { Id = Guid.NewGuid(), Key = "InviteMonthlyQuota", OldValue = null, NewValue = 20, ChangedAt = Now, ChangedBy = "Ana" });
            await repository.SaveAsync(new PlatformSetting { Key = "InviteMonthlyQuota", Value = 25, UpdatedAt = Now.AddMinutes(1), UpdatedBy = "Bia" },
                new PlatformSettingChange { Id = Guid.NewGuid(), Key = "InviteMonthlyQuota", OldValue = 20, NewValue = 25, ChangedAt = Now.AddMinutes(1), ChangedBy = "Bia", Note = "calibragem" });

            var saved = Assert.Single(await new SettingsRepository(make()).ListAsync());
            Assert.Equal((25, "Bia"), (saved.Value, saved.UpdatedBy));                                                   // trocou, não duplicou

            await repository.RemoveAsync("InviteMonthlyQuota", new PlatformSettingChange { Id = Guid.NewGuid(), Key = "InviteMonthlyQuota", OldValue = 25, NewValue = null, ChangedAt = Now.AddMinutes(2), ChangedBy = "Ana" });

            Assert.Empty(await new SettingsRepository(make()).ListAsync());                                              // voltou ao padrão: a linha some
            var changes = await new SettingsRepository(make()).ListChangesAsync(10);
            Assert.Equal(new int?[] { null, 20, 25 }, changes.OrderBy(c => c.ChangedAt).Select(c => c.OldValue).ToArray());                      // as três mudanças ficaram, da mais velha à mais nova
            Assert.Equal("Ana", changes.First().ChangedBy);                                                              // a mais recente primeiro
        }
        finally { await drop(); }
    }
}
