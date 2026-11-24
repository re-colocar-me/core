using core.domain.Entities;
using core.domain.Enums;
using core.infrastructure;
using core.infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace core.Tests;

// Dentro do namespace: `core.Profile` (a classe gRPC) venceria um alias declarado fora dele.
using Profile = core.domain.Entities.Profile;
using Telephone = core.domain.Telephone;
using Consultant = core.domain.Entities.Consultant;
using ConsultantErasureReport = core.domain.Interfaces.Infrastructure.ConsultantErasureReport;

/// <summary>
/// Só roda quando a variável de ambiente <c>CORE_TEST_PG</c> aponta para um Postgres (por exemplo <c>Host=localhost;Port=5432;Username=postgres;Password=...</c>, sem o banco). Sem ela, o teste é
/// <b>pulado</b> e o resultado diz por quê. Cria um banco novo só para o teste, aplica as migrações e o apaga no fim: nunca toca em dado de ninguém.
/// </summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public const string Variable = "CORE_TEST_PG";

    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
            Skip = $"Defina {Variable} com a conexão de um Postgres (sem o banco) para rodar esta prova.";
    }
}

/// <summary>
/// A PROVA em Postgres real da exclusão de conta do consultor (docs/specs/exclusao-de-conta-consultor.md, EC2 e seção 6): semeia <b>dois</b> consultores com todos os tipos de dado (o alvo e um que
/// <b>não pode ser tocado</b>), executa o apagador e confere linha por linha o que saiu, o que foi anonimizado e o que ficou. Também prova o pedido (colunas e <c>UPDATE</c>s condicionais) e as contagens de
/// atendimentos em andamento. <b>Escrito sem poder rodar no momento da autoria (sem Postgres na máquina)</b>: a primeira execução pode apontar erro de semeadura antes de apontar erro do apagador.
/// </summary>
public class ConsultantErasurePostgresTests
{
    private static readonly DateTime Now = new(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);

    private sealed record Graph(Guid ProfileId, Guid ConsultantId, Guid TalentId, Guid PurchaseId, Guid OfferingId, Guid ConnectionId, Guid OpenConversationId, Guid InviteConversationId);

    private static async Task<(ReColocarmeContext Context, Func<ReColocarmeContext> Fresh, Func<Task> Drop)> CreateDatabaseAsync()
    {
        var admin = Environment.GetEnvironmentVariable(PostgresFactAttribute.Variable)!;
        var name = "core_erasure_" + Guid.NewGuid().ToString("N")[..10];

        await using (var connection = new NpgsqlConnection(admin + ";Database=postgres"))
        {
            await connection.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await create.ExecuteNonQueryAsync();
        }

        var cs = admin + ";Database=" + name;
        ReColocarmeContext Make() => new(new DbContextOptionsBuilder<ReColocarmeContext>().UseNpgsql(cs).Options);

        var context = Make();
        await context.Database.MigrateAsync();

        async Task Drop()
        {
            await context.DisposeAsync();
            NpgsqlConnection.ClearAllPools();
            await using var connection = new NpgsqlConnection(admin + ";Database=postgres");
            await connection.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", connection);
            await drop.ExecuteNonQueryAsync();
        }

        return (context, Make, Drop);
    }

    /// <summary>Um consultor completo: tudo o que a spec manda apagar, anonimizar ou manter existe aqui.</summary>
    private static async Task<Graph> SeedAsync(ReColocarmeContext db, string email, string first, bool withLead)
    {
        var profileId = Guid.NewGuid();
        var consultantId = Guid.NewGuid();
        var talentId = Guid.NewGuid();

        var skill = new Skill { Id = Guid.NewGuid(), Name = "Liderança " + first };
        var category = new ServiceCategory { Id = Guid.NewGuid(), Name = "Carreira " + first };
        var item = new ConsultantServiceItem { Id = Guid.NewGuid(), Name = "Revisão de currículo " + first, CategoryId = category.Id };
        db.AddRange(skill, category, item);

        db.Profiles.Add(new Profile
        {
            Id = profileId, Email = email, Name = new PersonName { FirstName = first, LastName = "Costa" }, PictureUrl = "https://cdn.exemplo.com/" + first + ".jpg", BioText = "Coach de carreira",
            Role = RoleType.Consultant, PlatformId = "li-" + first, Telephone = new Telephone { CountryCode = "55", AreaCode = "11", Number = "999990000" }, BirthDate = new DateTime(1985, 5, 5, 0, 0, 0, DateTimeKind.Utc),
            GenderCode = "F", TermsAcceptedAt = Now.AddDays(-90), TermsAcceptedVersion = "v1", DeletionRequestedAt = Now.AddDays(-8), DeletionScheduledFor = Now.AddDays(-1), LastAccess = Now.AddDays(-9)
        });

        var offering = new ConsultantServiceOffering { Id = Guid.NewGuid(), ConsultantId = consultantId, ServiceItemId = item.Id, PriceInLemonCoins = 150 };
        db.Consultants.Add(new Consultant
        {
            Id = consultantId, ProfileId = profileId, Name = new PersonName { FirstName = first, LastName = "Costa" },
            Resume = new Resume { Summary = "Resumo de " + first, Contact = new Contacts { Email = email, LinkedIn = "linkedin.com/in/" + first } },
            Skills = [skill], ProvidedServices = [offering],
            Availabilities = [new Availability { Id = Guid.NewGuid(), OwnerId = consultantId, WeekDay = "Monday", StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(10, 0), ServiceId = item.Id }]
        });
        db.ServiceOfferingPriceHistories.Add(new ServiceOfferingPriceHistory { Id = Guid.NewGuid(), OfferingId = offering.Id, Price = 150 });
        db.SporadicAvailabilities.Add(new SporadicAvailability { Id = Guid.NewGuid(), OwnerId = consultantId, Date = DateOnly.FromDateTime(Now), StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(10, 0), ServiceId = item.Id });
        db.ConsultantAudiences.Add(new ConsultantAudience { ConsultantId = consultantId, Criteria = new() { ["situation"] = ["laid-off"] }, UpdatedAt = Now });
        db.FirstConversationConfigs.Add(new FirstConversationConfig { ConsultantId = consultantId, Enabled = true, DurationMinutes = 30, MonthlyQuota = 4, UpdatedAt = Now });

        var purchase = new ServicePurchase
        {
            Id = Guid.NewGuid(), CandidateId = talentId, Candidate = new CandidateSnapshot { Name = new PersonName { FirstName = "Ana", LastName = "Lima" }, Email = "ana@exemplo.com" },
            ConsultantId = consultantId, OfferingId = offering.Id, ServiceName = item.Name, TotalSessions = 1, UnitPriceInLemonCoins = 150, TotalPriceInLemonCoins = 150, WalletDebitTransactionId = Guid.NewGuid()
        };
        db.ServicePurchases.Add(purchase);
        db.Schedules.Add(new Schedule
        {
            Id = Guid.NewGuid(), OwnerId = profileId, Recipient = new ScheduleRecipient { Name = new PersonName { FirstName = "Ana", LastName = "Lima" } }, StartTime = Now.AddDays(-20), EndTime = Now.AddDays(-20).AddHours(1),
            Notes = "anotação privada do consultor", ServicePurchaseId = purchase.Id, Status = ScheduleStatus.Confirmed
        });

        var connection = new Connection
        {
            Id = Guid.NewGuid(), ConsultantId = consultantId, CandidateId = talentId, ShareSwotAnalysis = true,
            Candidate = new CandidateSnapshot { Name = new PersonName { FirstName = "Ana", LastName = "Lima" }, Email = "ana@exemplo.com", PictureUrl = "https://cdn.exemplo.com/ana.jpg", Telephone = new Telephone { CountryCode = "55", AreaCode = "21", Number = "888880000" } }
        };
        db.Connections.Add(connection);
        var tratativa = new Tratativa { Id = Guid.NewGuid(), ConnectionId = connection.Id, ServicePurchaseId = purchase.Id, Stage = TratativaStage.ProcessoFinalizado, Observacao = "nota privada sobre a Ana" };
        db.Tratativas.Add(tratativa);
        db.TratativaStageChanges.Add(new TratativaStageChange { Id = Guid.NewGuid(), TratativaId = tratativa.Id, ToStage = TratativaStage.ProcessoFinalizado });

        db.ConsultantReviews.Add(new ConsultantReview
        {
            Id = Guid.NewGuid(), Kind = ReviewKind.Overall, ConsultantId = consultantId, CandidateId = talentId, ServicePurchaseId = purchase.Id, Rating = 5, ServiceName = item.Name, Text = "Excelente", AuthorDisplayName = "Ana L."
        });
        db.ConsultantRatingSummaries.Add(new ConsultantRatingSummary { ConsultantId = consultantId, Count = 1, Sum = 5 });

        var open = new Conversation
        {
            Id = Guid.NewGuid(), ConsultantId = consultantId, CandidateId = Guid.NewGuid(), Origin = ConversationOrigin.HandRaised, Status = ConversationStatus.Raised, CreatedAt = Now.AddDays(-9), UpdatedAt = Now.AddDays(-9),
            ExpiresAt = Now.AddDays(5), Note = "frase do talento",
            Card = new ConversationProfileCard { Situation = "desligado" },
            Events = [new ConversationEvent { Id = Guid.NewGuid(), Type = "Raised", Actor = "Candidate", At = Now.AddDays(-9) }]
        };
        var invite = new Conversation
        {
            Id = Guid.NewGuid(), ConsultantId = consultantId, CandidateId = Guid.NewGuid(), Origin = ConversationOrigin.Invite, Status = ConversationStatus.Held, CreatedAt = Now.AddDays(-9), UpdatedAt = Now.AddDays(-9),
            InviteText = "Olá, posso te ajudar", InviteReason = "Combina com você",
            Events = [new ConversationEvent { Id = Guid.NewGuid(), Type = "Sent", Actor = "Consultant", At = Now.AddDays(-9) }]
        };
        db.Conversations.AddRange(open, invite);
        await db.SaveChangesAsync();

        db.ConversationReports.AddRange(
            new ConversationReport { Id = Guid.NewGuid(), ConversationId = open.Id, ConsultantId = consultantId, CandidateId = open.CandidateId, ReporterRole = ReporterRoles.Consultant, Origin = ConversationOrigin.HandRaised, Reason = ReportReason.Spam, Details = "texto escrito pelo consultor", CreatedAt = Now.AddDays(-9) },
            new ConversationReport { Id = Guid.NewGuid(), ConversationId = invite.Id, ConsultantId = consultantId, CandidateId = invite.CandidateId, ReporterRole = ReporterRoles.Candidate, Origin = ConversationOrigin.Invite, Reason = ReportReason.Offensive, Details = "texto escrito pelo talento", CreatedAt = Now.AddDays(-9) });
        db.TalentSilencedConsultants.Add(new TalentSilencedConsultant { CandidateId = talentId, ConsultantId = consultantId, At = Now.AddDays(-30) });
        db.Notifications.Add(new NotificationEntity { Id = Guid.NewGuid(), Owner = profileId, Title = "Olá", Body = "corpo" });
        if (withLead)
            db.Leads.Add(new Lead { Id = Guid.NewGuid(), Kind = LeadKind.Consultor, Name = first, Email = email.ToUpperInvariant(), ConsentedAt = Now.AddDays(-100) });
        await db.SaveChangesAsync();

        return new Graph(profileId, consultantId, talentId, purchase.Id, offering.Id, connection.Id, open.Id, invite.Id);
    }

    [PostgresFact]
    public async Task O_apagador_remove_o_que_e_do_consultor_anonimiza_o_que_fala_de_outros_mantem_o_que_a_lei_e_o_historico_pedem_e_nao_toca_em_mais_ninguem()
    {
        var (setup, fresh, drop) = await CreateDatabaseAsync();
        try
        {
            var target = await SeedAsync(setup, "marina@exemplo.com", "Marina", withLead: true);
            var other = await SeedAsync(setup, "outro@exemplo.com", "Outro", withLead: true);
            setup.Leads.Add(new Lead { Id = Guid.NewGuid(), Kind = LeadKind.Candidato, Name = "Marina", Email = "marina@exemplo.com", ConsentedAt = Now });      // mesmo e-mail, mas é lista de espera de talento: não é dele
            await setup.SaveChangesAsync();

            ConsultantErasureReport report;
            await using (var db = fresh())
                report = await new ConsultantErasureRepository(db).EraseDataAsync(target.ProfileId, Now);

            await using var check = fresh();

            // ---- o relatório conta o que foi feito ----
            Assert.False(report.AlreadyCompleted);
            Assert.True(report.ConsultantFound);
            Assert.Equal(2, report.Availabilities);                                   // uma semanal e uma avulsa
            Assert.Equal(1, report.Offerings);
            Assert.Equal(1, report.Reviews);
            Assert.Equal(1, report.Tratativas);
            Assert.Equal(1, report.ConnectionsAnonymized);
            Assert.Equal(2, report.ConversationsClosed);                              // a mão levantada aberta e o convite em espera
            Assert.Equal(2, report.ConversationsAnonymized);
            Assert.Equal(1, report.Notifications);
            Assert.Equal(1, report.Leads);
            Assert.Equal(1, report.SilencingsRemoved);

            // ---- apagado ----
            var consultant = await check.Consultants.Include(x => x.Skills).SingleAsync(x => x.Id == target.ConsultantId);
            Assert.Null(consultant.Resume);                                           // o currículo, com identificação e contatos
            Assert.Empty(consultant.Skills);
            Assert.Equal(0, await check.Availabilities.CountAsync(x => x.OwnerId == target.ConsultantId));
            Assert.Equal(0, await check.SporadicAvailabilities.CountAsync(x => x.OwnerId == target.ConsultantId));
            Assert.Equal(0, await check.ConsultantServiceOfferings.CountAsync(x => x.ConsultantId == target.ConsultantId));
            Assert.Equal(0, await check.ServiceOfferingPriceHistories.CountAsync(x => x.OfferingId == target.OfferingId));
            Assert.Equal(0, await check.ConsultantAudiences.CountAsync(x => x.ConsultantId == target.ConsultantId));
            Assert.Equal(0, await check.FirstConversationConfigs.CountAsync(x => x.ConsultantId == target.ConsultantId));
            Assert.Equal(0, await check.ConsultantReviews.CountAsync(x => x.ConsultantId == target.ConsultantId));
            Assert.Equal(0, await check.ConsultantRatingSummaries.CountAsync(x => x.ConsultantId == target.ConsultantId));
            Assert.Equal(0, await check.Tratativas.CountAsync(x => x.ConnectionId == target.ConnectionId));
            Assert.Equal(0, await check.Notifications.CountAsync(x => x.Owner == target.ProfileId));
            Assert.Equal(0, await check.TalentSilencedConsultants.CountAsync(x => x.ConsultantId == target.ConsultantId));
            Assert.Equal(1, await check.Leads.CountAsync(x => x.Kind == LeadKind.Consultor));                            // só o do OUTRO consultor sobra
            Assert.Equal(1, await check.Leads.CountAsync(x => x.Kind == LeadKind.Candidato));                            // o do talento com o mesmo e-mail NÃO é tocado

            // ---- anonimizado ----
            var connection = await check.Connections.SingleAsync(x => x.Id == target.ConnectionId);
            Assert.Equal("Conta", connection.Candidate.Name.FirstName);
            Assert.Null(connection.Candidate.Email);
            Assert.Null(connection.Candidate.PictureUrl);
            Assert.Null(connection.Candidate.Telephone);
            Assert.False(connection.ShareSwotAnalysis);

            var conversations = await check.Conversations.Where(x => x.ConsultantId == target.ConsultantId).ToListAsync();
            Assert.Equal(2, conversations.Count);                                                                          // a linha FICA (cota e "uma por par")
            Assert.All(conversations, c =>
            {
                Assert.Equal(ConversationStatus.Expired, c.Status);                                                        // as abertas foram encerradas
                Assert.NotNull(c.AnonymizedAt);
                Assert.Null(c.Note);
                Assert.Null(c.InviteText);
                Assert.Null(c.InviteReason);
            });
            Assert.DoesNotContain(conversations, c => c.CandidateId == Guid.Empty);
            Assert.Equal(0, await check.ConversationEvents.CountAsync(x => x.ConversationId == target.OpenConversationId || x.ConversationId == target.InviteConversationId));
            Assert.Equal(0, await check.Set<ConversationProfileCard>().CountAsync(x => x.ConversationId == target.OpenConversationId));

            var byConsultant = await check.ConversationReports.SingleAsync(x => x.ConsultantId == target.ConsultantId && x.ReporterRole == ReporterRoles.Consultant);
            var againstHim = await check.ConversationReports.SingleAsync(x => x.ConsultantId == target.ConsultantId && x.ReporterRole == ReporterRoles.Candidate);
            Assert.Null(byConsultant.Details);                                                                             // o texto dele sai
            Assert.Equal("texto escrito pelo talento", againstHim.Details);                                                // a denúncia contra ele continua (prova)

            Assert.Null((await check.Schedules.SingleAsync(x => x.OwnerId == target.ProfileId)).Notes);                   // a anotação sai...
            Assert.Equal("Ana", (await check.Schedules.SingleAsync(x => x.OwnerId == target.ProfileId)).Recipient.Name.FirstName);   // ...a sessão passada fica

            // ---- mantido ----
            Assert.Equal(1, await check.ServicePurchases.CountAsync(x => x.Id == target.PurchaseId));                      // a compra (contabilidade e histórico do talento)
            Assert.Equal("ana@exemplo.com", (await check.ServicePurchases.SingleAsync(x => x.Id == target.PurchaseId)).Candidate.Email);   // é do talento, não do consultor

            // ---- o perfil NÃO é tocado em EraseData: só na conclusão ----
            var profile = await check.Profiles.SingleAsync(x => x.Id == target.ProfileId);
            Assert.Equal("marina@exemplo.com", profile.Email);
            Assert.Equal("li-Marina", profile.PlatformId);
            Assert.Null(profile.DeletionCompletedAt);

            // ---- ninguém mais foi tocado ----
            var untouched = await check.Consultants.Include(x => x.Skills).SingleAsync(x => x.Id == other.ConsultantId);
            Assert.NotNull(untouched.Resume);
            Assert.Single(untouched.Skills);
            Assert.Equal(1, await check.Availabilities.CountAsync(x => x.OwnerId == other.ConsultantId));
            Assert.Equal(1, await check.ConsultantServiceOfferings.CountAsync(x => x.ConsultantId == other.ConsultantId));
            Assert.Equal(1, await check.ConsultantReviews.CountAsync(x => x.ConsultantId == other.ConsultantId));
            Assert.Equal(1, await check.Tratativas.CountAsync(x => x.ConnectionId == other.ConnectionId));
            Assert.Equal(1, await check.TratativaStageChanges.CountAsync());                                              // sobrou exatamente a do OUTRO
            Assert.Equal(1, await check.Notifications.CountAsync(x => x.Owner == other.ProfileId));
            Assert.Equal("Ana", (await check.Connections.SingleAsync(x => x.Id == other.ConnectionId)).Candidate.Name.FirstName);
            Assert.All(await check.Conversations.Where(x => x.ConsultantId == other.ConsultantId).ToListAsync(), c => Assert.Null(c.AnonymizedAt));
            Assert.NotNull((await check.ConversationReports.FirstAsync(x => x.ConsultantId == other.ConsultantId && x.ReporterRole == ReporterRoles.Consultant)).Details);
            Assert.Equal("Outro", (await check.Profiles.SingleAsync(x => x.Id == other.ProfileId)).Name.FirstName);
        }
        finally
        {
            await drop();
        }
    }

    [PostgresFact]
    public async Task A_apagacao_marca_Deleting_na_mesma_transacao_o_pedido_nao_se_cancela_mais_o_login_ve_Deleting_e_a_conclusao_grava_Inactive()
    {
        var (setup, fresh, drop) = await CreateDatabaseAsync();
        try
        {
            var target = await SeedAsync(setup, "marina@exemplo.com", "Marina", withLead: false);
            var other = await SeedAsync(setup, "outro@exemplo.com", "Outro", withLead: false);

            // Antes: Active, e o pedido SE cancelaria (prova de que a recusa abaixo vem do Deleting, não de outra coisa).
            await using (var db = fresh())
            {
                Assert.Equal(ProfileStatus.Active, (await db.Profiles.SingleAsync(x => x.Id == target.ProfileId)).Status);
                Assert.False((await new ConsultantDeletionRepository(db).FindProfileAsync(target.ProfileId))!.ErasureStarted);
            }

            await using (var db = fresh())
                await new ConsultantErasureRepository(db).EraseDataAsync(target.ProfileId, Now);

            // Depois: só o alvo ficou Deleting; o outro consultor continua Active e a linha enxerga a apagação como começada.
            await using (var db = fresh())
            {
                var profile = await db.Profiles.SingleAsync(x => x.Id == target.ProfileId);
                Assert.Equal(ProfileStatus.Deleting, profile.Status);
                Assert.Equal(Now, profile.ModifiedAt);
                Assert.Equal(ProfileStatus.Active, (await db.Profiles.SingleAsync(x => x.Id == other.ProfileId)).Status);
                Assert.True((await new ConsultantDeletionRepository(db).FindProfileAsync(target.ProfileId))!.ErasureStarted);
                Assert.False((await new ConsultantDeletionRepository(db).FindProfileAsync(other.ProfileId))!.ErasureStarted);
            }

            // O pedido NÃO se cancela mais (o do outro consultor, sim): a exclusão só anda para a frente.
            await using (var db = fresh())
                Assert.False(await new ConsultantDeletionRepository(db).TryCancelAsync(target.ProfileId));
            await using (var db = fresh())
            {
                Assert.NotNull((await db.Profiles.SingleAsync(x => x.Id == target.ProfileId)).DeletionRequestedAt);
                Assert.True(await new ConsultantDeletionRepository(db).TryCancelAsync(other.ProfileId));
            }

            // Repetir o apagador continua seguro e o perfil continua Deleting (não volta, não pula para Inactive).
            await using (var db = fresh())
                await new ConsultantErasureRepository(db).EraseDataAsync(target.ProfileId, Now.AddHours(1));
            await using (var db = fresh())
                Assert.Equal(ProfileStatus.Deleting, (await db.Profiles.SingleAsync(x => x.Id == target.ProfileId)).Status);

            // A conclusão grava Inactive.
            await using (var db = fresh())
                Assert.True(await new ConsultantErasureRepository(db).CompleteAsync(target.ProfileId, Now.AddHours(2)));
            await using (var db = fresh())
                Assert.Equal(ProfileStatus.Inactive, (await db.Profiles.SingleAsync(x => x.Id == target.ProfileId)).Status);
        }
        finally { await drop(); }
    }

    [PostgresFact]
    public async Task Repetir_o_apagador_e_seguro_a_conclusao_anonimiza_o_perfil_so_depois_e_so_uma_vez_e_o_talento_e_o_outro_consultor_ficam_de_fora()
    {
        var (setup, fresh, drop) = await CreateDatabaseAsync();
        try
        {
            var target = await SeedAsync(setup, "marina@exemplo.com", "Marina", withLead: false);
            var talentProfile = new Profile { Id = Guid.NewGuid(), Email = "talento@exemplo.com", Name = new PersonName { FirstName = "Talento", LastName = "X" }, Role = RoleType.Talent };
            setup.Profiles.Add(talentProfile);
            await setup.SaveChangesAsync();

            await using (var db = fresh())
                await new ConsultantErasureRepository(db).EraseDataAsync(target.ProfileId, Now);

            // Segunda execução: nada a apagar, nada quebra.
            ConsultantErasureReport second;
            await using (var db = fresh())
                second = await new ConsultantErasureRepository(db).EraseDataAsync(target.ProfileId, Now);
            Assert.Equal((0, 0, 0, 0, 0), (second.Availabilities, second.Offerings, second.Reviews, second.Tratativas, second.ConnectionsAnonymized));
            Assert.Equal(0, second.ConversationsClosed);
            Assert.Equal(0, second.ConversationsAnonymized);                          // as já anonimizadas não são refeitas

            await using (var db = fresh())
                Assert.True(await new ConsultantErasureRepository(db).IsDataErasedAsync(target.ProfileId));

            // Concluir: anonimiza o perfil e o nome do consultor, grava a data, e uma segunda conclusão não faz nada.
            await using (var db = fresh())
                Assert.True(await new ConsultantErasureRepository(db).CompleteAsync(target.ProfileId, Now.AddMinutes(1)));
            await using (var db = fresh())
                Assert.False(await new ConsultantErasureRepository(db).CompleteAsync(target.ProfileId, Now.AddMinutes(2)));

            await using var check = fresh();
            var profile = await check.Profiles.SingleAsync(x => x.Id == target.ProfileId);
            Assert.Equal(string.Empty, profile.Email);
            Assert.Equal("Consultor", profile.Name.FirstName);
            Assert.Equal("desligado", profile.Name.LastName);
            Assert.Equal(string.Empty, profile.PictureUrl);
            Assert.Null(profile.BioText);
            Assert.Null(profile.Telephone);
            Assert.Null(profile.BirthDate);
            Assert.Null(profile.GenderCode);
            Assert.Null(profile.PlatformId);                                                                               // um novo login com o mesmo LinkedIn cria uma conta nova
            Assert.Null(profile.TermsAcceptedAt);
            Assert.Equal(ProfileStatus.Inactive, profile.Status);
            Assert.Equal(Now.AddMinutes(1), profile.DeletionCompletedAt);                                                  // a data da PRIMEIRA conclusão
            Assert.Equal("Consultor", (await check.Consultants.SingleAsync(x => x.Id == target.ConsultantId)).Name.FirstName);

            // O erase repetido depois de concluída devolve "já concluída" e não faz nada.
            await using (var db = fresh())
                Assert.True((await new ConsultantErasureRepository(db).EraseDataAsync(target.ProfileId, Now)).AlreadyCompleted);

            // Recusas: um talento nunca é apagado por este caminho.
            await using (var db = fresh())
                await Assert.ThrowsAsync<core.domain.Interfaces.Infrastructure.AccountErasureRefusedException>(() => new ConsultantErasureRepository(db).EraseDataAsync(talentProfile.Id, Now));
            Assert.Equal("Talento", (await check.Profiles.SingleAsync(x => x.Id == talentProfile.Id)).Name.FirstName);
        }
        finally
        {
            await drop();
        }
    }

    [PostgresFact]
    public async Task A_lista_dos_vencidos_traz_so_consultor_com_carencia_acabada_e_sem_conclusao_do_mais_antigo_ao_mais_novo()
    {
        var (setup, fresh, drop) = await CreateDatabaseAsync();
        try
        {
            var due = await SeedAsync(setup, "vencido@exemplo.com", "Vencido", withLead: false);                // pedido de 8 dias atrás, carência acabou ontem
            var older = await SeedAsync(setup, "maisantigo@exemplo.com", "Antigo", withLead: false);
            var inGrace = await SeedAsync(setup, "carencia@exemplo.com", "Carencia", withLead: false);
            var done = await SeedAsync(setup, "concluido@exemplo.com", "Concluido", withLead: false);
            var talent = new Profile { Id = Guid.NewGuid(), Email = "talento@exemplo.com", Name = new PersonName { FirstName = "T", LastName = "X" }, Role = RoleType.Talent, DeletionRequestedAt = Now.AddDays(-20), DeletionScheduledFor = Now.AddDays(-13) };

            await using (var db = fresh())
            {
                await db.Profiles.Where(x => x.Id == older.ProfileId).ExecuteUpdateAsync(s => s.SetProperty(x => x.DeletionScheduledFor, Now.AddDays(-5)));
                await db.Profiles.Where(x => x.Id == inGrace.ProfileId).ExecuteUpdateAsync(s => s.SetProperty(x => x.DeletionScheduledFor, Now.AddDays(2)));
                await db.Profiles.Where(x => x.Id == done.ProfileId).ExecuteUpdateAsync(s => s.SetProperty(x => x.DeletionCompletedAt, Now.AddDays(-1)));
                db.Profiles.Add(talent);
                await db.SaveChangesAsync();
            }

            await using var read = fresh();
            var repository = new ConsultantDeletionRepository(read);

            Assert.Equal([older.ProfileId, due.ProfileId], await repository.ListDueAsync(Now, 10));             // do que venceu há mais tempo; sem carência, concluído nem talento
            Assert.Equal([older.ProfileId], await repository.ListDueAsync(Now, 1));                            // o tamanho da rodada vale
            Assert.Empty(await repository.ListDueAsync(Now.AddDays(-10), 10));                                  // antes de qualquer carência acabar
        }
        finally
        {
            await drop();
        }
    }

    [PostgresFact]
    public async Task O_pedido_e_o_que_impede_funcionam_no_banco_de_verdade_colunas_updates_condicionais_e_as_duas_contagens()
    {
        var (setup, fresh, drop) = await CreateDatabaseAsync();
        try
        {
            var target = await SeedAsync(setup, "marina@exemplo.com", "Marina", withLead: false);

            // O pedido: só grava se ainda não há pedido (a semente já tem um: nada é regravado) e o cancelamento limpa.
            await using (var db = fresh())
            {
                var repository = new ConsultantDeletionRepository(db);
                Assert.False(await repository.TryRequestAsync(target.ProfileId, Now, Now.AddDays(7)));
                Assert.True(await repository.TryCancelAsync(target.ProfileId));
                Assert.False(await repository.TryCancelAsync(target.ProfileId));
                Assert.True(await repository.TryRequestAsync(target.ProfileId, Now, Now.AddDays(7)));
                Assert.False(await repository.TryRequestAsync(target.ProfileId, Now.AddDays(1), Now.AddDays(8)));            // pedir de novo não renova

                var row = await repository.FindProfileAsync(target.ProfileId);
                Assert.Equal(Now, row!.RequestedAt);
                Assert.Equal(Now.AddDays(7), row.ScheduledFor);
                Assert.Equal(RoleType.Consultant, row.Role);
            }

            // O que impede: a tratativa da semente está em Finalizado (não conta); em Processo Ativo conta; uma sessão futura não cancelada conta.
            await using (var db = fresh())
            {
                var repository = new ConsultantDeletionRepository(db);
                Assert.Equal(0, await repository.CountOpenEngagementsAsync(target.ProfileId));
                Assert.Equal(0, await repository.CountFutureSessionsAsync(target.ProfileId, Now));
            }
            await using (var db = fresh())
            {
                await db.Tratativas.Where(x => x.ConnectionId == target.ConnectionId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Stage, TratativaStage.ProcessoAtivo));
                db.Schedules.Add(new Schedule { Id = Guid.NewGuid(), OwnerId = target.ProfileId, Recipient = new ScheduleRecipient { Name = new PersonName { FirstName = "Ana", LastName = "Lima" } }, StartTime = Now.AddDays(2), EndTime = Now.AddDays(2).AddHours(1), Status = ScheduleStatus.Pending });
                db.Schedules.Add(new Schedule { Id = Guid.NewGuid(), OwnerId = target.ProfileId, Recipient = new ScheduleRecipient { Name = new PersonName { FirstName = "Ana", LastName = "Lima" } }, StartTime = Now.AddDays(3), EndTime = Now.AddDays(3).AddHours(1), Status = ScheduleStatus.Canceled });
                await db.SaveChangesAsync();
            }
            await using (var db = fresh())
            {
                var repository = new ConsultantDeletionRepository(db);
                Assert.Equal(1, await repository.CountOpenEngagementsAsync(target.ProfileId));
                Assert.Equal(1, await repository.CountFutureSessionsAsync(target.ProfileId, Now));                          // a cancelada não conta
                Assert.Equal(0, await repository.CountFutureSessionsAsync(target.ProfileId, Now.AddDays(4)));               // depois do fim, já não é futura
            }
        }
        finally
        {
            await drop();
        }
    }
}
