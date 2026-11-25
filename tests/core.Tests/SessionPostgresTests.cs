using core.domain.Entities;
using core.domain.Enums;
using core.domain.Services.Sessions;
using core.infrastructure;
using core.infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace core.Tests;

/// <summary>A sessão em vídeo num Postgres de verdade (docs/specs/sessao-em-video-e-chat.md): a migração, o índice único, a versão da linha e o ciclo pelo serviço. Só com <c>CORE_TEST_PG</c>.</summary>
public class SessionPostgresTests
{
    private static readonly DateTime StartUtc = new(2026, 12, 1, 17, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WallStart = new(2026, 12, 1, 14, 0, 0, DateTimeKind.Utc);      // 14h de Brasília

    private sealed class Clock : TimeProvider
    {
        public DateTime Now { get; set; } = StartUtc;
        public override DateTimeOffset GetUtcNow() => new(Now);
    }

    private static async Task<(Func<ReColocarmeContext> Fresh, Func<Task> Drop)> CreateDatabaseAsync()
    {
        var admin = Environment.GetEnvironmentVariable(PostgresFactAttribute.Variable)!;
        var name = "core_session_" + Guid.NewGuid().ToString("N")[..10];
        await using (var connection = new NpgsqlConnection(admin + ";Database=postgres"))
        {
            await connection.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await create.ExecuteNonQueryAsync();
        }

        ReColocarmeContext Make() => new(new DbContextOptionsBuilder<ReColocarmeContext>().UseNpgsql(admin + ";Database=" + name).Options);
        await using (var context = Make())
            await context.Database.MigrateAsync();

        return (Make, async () =>
        {
            NpgsqlConnection.ClearAllPools();
            await using var connection = new NpgsqlConnection(admin + ";Database=postgres");
            await connection.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", connection);
            await drop.ExecuteNonQueryAsync();
        });
    }

    private static async Task<(Guid ScheduleId, Guid ConsultantProfile, Guid Talent)> SeedAsync(ReColocarmeContext db)
    {
        var consultantProfile = Guid.NewGuid();
        var talent = Guid.NewGuid();
        var purchase = new ServicePurchase
        {
            Id = Guid.NewGuid(), CandidateId = talent, Candidate = new CandidateSnapshot { Name = new PersonName { FirstName = "Ana", LastName = "Lima" } }, ConsultantId = Guid.NewGuid(), OfferingId = Guid.NewGuid(),
            ServiceName = "Mentoria", TotalSessions = 1, UnitPriceInLemonCoins = 10, TotalPriceInLemonCoins = 10, WalletDebitTransactionId = Guid.NewGuid()
        };
        var schedule = new Schedule
        {
            Id = Guid.NewGuid(), OwnerId = consultantProfile, Recipient = new ScheduleRecipient { Name = new PersonName { FirstName = "Ana", LastName = "Lima" } }, StartTime = WallStart, EndTime = WallStart.AddMinutes(60),
            Status = ScheduleStatus.Confirmed, ServicePurchaseId = purchase.Id
        };
        db.ServicePurchases.Add(purchase);
        db.Schedules.Add(schedule);
        await db.SaveChangesAsync();
        return (schedule.Id, consultantProfile, talent);
    }

    [PostgresFact]
    public async Task A_sessao_grava_e_le_recusa_a_segunda_do_mesmo_horario_e_a_gravacao_sobre_estado_velho()
    {
        var (fresh, drop) = await CreateDatabaseAsync();
        try
        {
            Guid scheduleId, consultantProfile, talent;
            await using (var db = fresh()) (scheduleId, consultantProfile, talent) = await SeedAsync(db);

            await using (var db = fresh())
            {
                var row = await new SessionRepository(db).FindScheduleAsync(scheduleId);
                Assert.Equal((consultantProfile, talent, WallStart, ScheduleStatus.Confirmed, ServicePurchaseStatus.Active), (row!.ConsultantProfileId, row.CandidateId, row.StartWallClock, row.ScheduleStatus, row.PurchaseStatus));
                Assert.Null(await new SessionRepository(db).FindScheduleAsync(Guid.NewGuid()));
            }

            Session NewSession() => new() { Id = Guid.NewGuid(), ScheduleId = scheduleId, ServicePurchaseId = Guid.NewGuid(), ConsultantId = Guid.NewGuid(), CandidateId = talent, ScheduledStart = StartUtc, ScheduledEnd = StartUtc.AddMinutes(60) };
            SessionEvent Event(Session s) => new() { Id = Guid.NewGuid(), SessionId = s.Id, Actor = "talent", Type = "Join", At = StartUtc };

            var first = NewSession();
            await using (var db = fresh()) Assert.True(await new SessionRepository(db).TryAddAsync(first, Event(first)));
            var second = NewSession();
            await using (var db = fresh()) Assert.False(await new SessionRepository(db).TryAddAsync(second, Event(second)));

            // Dois lados leem o mesmo estado; o primeiro grava, o segundo é recusado e não deixa o fato dele para trás.
            await using var dbA = fresh();
            await using var dbB = fresh();
            var a = new SessionRepository(dbA);
            var b = new SessionRepository(dbB);
            var readA = (await a.GetByScheduleAsync(scheduleId))!;
            var readB = (await b.GetByScheduleAsync(scheduleId))!;
            Assert.NotEqual(0u, readA.Version);

            readA.Consultant.Connections = 1;
            readA.Consultant.LastSeenAt = StartUtc;
            Assert.True(await a.TrySaveAsync(readA, Event(readA)));
            readB.Talent.Connections = 1;
            Assert.False(await b.TrySaveAsync(readB, Event(readB)));

            var after = (await b.GetByScheduleAsync(scheduleId))!;
            Assert.Equal((1, 0, StartUtc), (after.Consultant.Connections, after.Talent.Connections, after.Consultant.LastSeenAt!.Value));
            await using (var db = fresh()) Assert.Equal(2, await db.SessionEvents.CountAsync());

            // Gravar de novo, em seguida, no mesmo repositório: a versão nova vale.
            after.Talent.Connections = 1;
            Assert.True(await b.TrySaveAsync(after, null));
            after.OverlapSeconds = 5;
            Assert.True(await b.TrySaveAsync(after, null));
        }
        finally { await drop(); }
    }

    [PostgresFact]
    public async Task A_exclusao_do_talento_mantem_a_sessao_e_o_resultado_e_apaga_a_trilha_e_o_id_da_reuniao_so_das_dele()
    {
        var (fresh, drop) = await CreateDatabaseAsync();
        try
        {
            var talent = Guid.NewGuid();
            Session Session(Guid candidateId) => new()
            {
                Id = Guid.NewGuid(), ScheduleId = Guid.NewGuid(), ServicePurchaseId = Guid.NewGuid(), ConsultantId = Guid.NewGuid(), CandidateId = candidateId, Status = SessionStatus.Completed,
                ScheduledStart = StartUtc, ScheduledEnd = StartUtc.AddHours(1), ClosedAt = StartUtc.AddHours(2), ChimeMeetingId = "reuniao-1", OverlapSeconds = 2000
            };
            var mine = Session(talent);
            var theirs = Session(Guid.NewGuid());
            await using (var db = fresh())
            {
                db.Sessions.AddRange(mine, theirs);
                db.SessionEvents.AddRange(
                    new SessionEvent { Id = Guid.NewGuid(), SessionId = mine.Id, Actor = "talent", Type = "Join", At = StartUtc },
                    new SessionEvent { Id = Guid.NewGuid(), SessionId = mine.Id, Actor = "consultant", Type = "Join", At = StartUtc },
                    new SessionEvent { Id = Guid.NewGuid(), SessionId = theirs.Id, Actor = "talent", Type = "Join", At = StartUtc });
                await db.SaveChangesAsync();
            }

            await using (var db = fresh()) await new AccountErasureRepository(db).EraseTalentAsync(talent);
            await using (var db = fresh()) await new AccountErasureRepository(db).EraseTalentAsync(talent);

            await using var check = fresh();
            var kept = await check.Sessions.AsNoTracking().SingleAsync(x => x.Id == mine.Id);
            Assert.Equal((SessionStatus.Completed, 2000, (string?)null), (kept.Status, kept.OverlapSeconds, kept.ChimeMeetingId));
            Assert.Equal(theirs.Id, Assert.Single(await check.SessionEvents.AsNoTracking().ToListAsync()).SessionId);
            Assert.Equal("reuniao-1", (await check.Sessions.AsNoTracking().SingleAsync(x => x.Id == theirs.Id)).ChimeMeetingId);
        }
        finally { await drop(); }
    }

    [PostgresFact]
    public async Task Pelo_servico_uma_sessao_inteira_entra_conta_o_tempo_junto_e_o_job_fecha_como_realizada()
    {
        var (fresh, drop) = await CreateDatabaseAsync();
        try
        {
            Guid scheduleId, consultantProfile, talent;
            await using (var db = fresh()) (scheduleId, consultantProfile, talent) = await SeedAsync(db);

            var clock = new Clock();
            await using var context = fresh();
            var service = new SessionService(new SessionRepository(context), clock, new SessionConfiguration());

            await service.SignalAsync(scheduleId, consultantProfile, PresenceSignal.Join);
            await service.SignalAsync(scheduleId, talent, PresenceSignal.Join);
            for (var half = 1; half <= 62; half++)      // 31 minutos de batimentos a cada 30 s
            {
                clock.Now = StartUtc.AddSeconds(half * 30);
                await service.SignalAsync(scheduleId, consultantProfile, PresenceSignal.Heartbeat);
                await service.SignalAsync(scheduleId, talent, PresenceSignal.Heartbeat);
            }

            clock.Now = StartUtc.AddMinutes(76);
            Assert.Equal(1, await service.CloseDueAsync());

            await using var check = fresh();
            var stored = await check.Sessions.AsNoTracking().SingleAsync();
            Assert.Equal((31 * 60, SessionStatus.Completed, false, "system"), (stored.OverlapSeconds, stored.Status, stored.DropFlag, stored.OutcomeSetBy));
            Assert.Equal(["Closed", "Join", "Join"], (await check.SessionEvents.AsNoTracking().Select(e => e.Type).ToListAsync()).Order(StringComparer.Ordinal));
            Assert.Equal(0, await service.CloseDueAsync());
        }
        finally { await drop(); }
    }
}
