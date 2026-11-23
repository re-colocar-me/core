using core.domain.Entities;
using core.domain.Enums;
using core.infrastructure;
using core.infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace core.Tests;

// Dentro do namespace: `core.Profile` (a classe gRPC) venceria um alias declarado fora dele.
using Profile = core.domain.Entities.Profile;
using Consultant = core.domain.Entities.Consultant;

/// <summary>
/// "Quem novos talentos podem ver" (docs/specs/exclusao-de-conta-consultor.md, D-EC10 e seção 17): o consultor com a exclusão pedida (em carência) ou concluída <b>some da busca, do match, do pagamento e
/// dos convites</b>; cancelar o pedido o devolve; o que já está em andamento (conexões) <b>segue</b>. Os testes de SQL conferem que <b>cada listagem aplica o mesmo filtro</b>; as provas em Postgres real
/// (<c>CORE_TEST_PG</c>) conferem o resultado de cada uma com consultores em quatro estados.
/// </summary>
public class ConsultantVisibilityTests
{
    private static ReColocarmeContext Context()
        => new(new DbContextOptionsBuilder<ReColocarmeContext>().UseNpgsql("Host=localhost;Database=nao-existe;Username=x;Password=x").Options);

    private static void AssertFiltersLeaving(string sql)
    {
        Assert.Contains("\"DeletionRequestedAt\" IS NOT NULL", sql);
        Assert.Contains("\"DeletionCompletedAt\" IS NOT NULL", sql);
        Assert.Contains("NOT EXISTS", sql);
    }

    // ---- o SQL: cada listagem de consultores para novos talentos aplica o mesmo filtro --------------------------------------------

    [Fact]
    public void A_busca_de_consultores_aplica_o_filtro()
    {
        using var context = Context();
        var sql = context.Consultants.VisibleToTalents(context).ToQueryString();

        AssertFiltersLeaving(sql);
        Assert.Contains("FROM \"Users\"", sql);                                  // o Profile mora na tabela Users
    }

    [Fact]
    public void O_filtro_de_existencia_olha_o_consultor_e_o_mesmo_filtro()
    {
        using var context = Context();
        var sql = context.Consultants.Where(k => k.Id == Guid.Empty).VisibleToTalents(context).ToQueryString();

        AssertFiltersLeaving(sql);
        Assert.Contains("\"ProfileId\"", sql);
    }

    // Os repositórios de verdade são testados no Postgres abaixo; aqui garantimos que a regra existe no código de cada listagem, para ninguém tirar sem um teste vermelho.
    [Theory]
    [InlineData(@"core.infrastructure\Repositories\ConsultantServicesRepository.cs", "FilterConsultantsAsync")]
    [InlineData(@"core.infrastructure\Repositories\MatchRepository.cs", "ListConsultantsAsync")]
    public void Cada_listagem_de_consultores_para_talentos_usa_o_filtro_central(string relativePath, string method)
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, relativePath));
        var start = source.IndexOf(method, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{method} não encontrado");
        var body = source[start..Math.Min(source.Length, start + 900)];

        Assert.Contains(".VisibleToTalents(_context)", body);
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "core.infrastructure"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Raiz dos repositórios não encontrada.");
    }

    // ---- o Postgres real -----------------------------------------------------------------------------------------------------------

    private sealed record Seeded(Guid ProfileId, Guid ConsultantId, Guid OfferingId);

    private static async Task<(ReColocarmeContext Context, Func<ReColocarmeContext> Fresh, Func<Task> Drop)> CreateDatabaseAsync()
    {
        var admin = Environment.GetEnvironmentVariable(PostgresFactAttribute.Variable)!;
        var name = "core_visibility_" + Guid.NewGuid().ToString("N")[..10];

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

    /// <summary>Um consultor pronto para a busca, o match e o convite (serviço, horário e currículo), com o estado do pedido de exclusão que o teste quiser.</summary>
    private static async Task<Seeded> SeedAsync(ReColocarmeContext db, string first, DateTime? requestedAt = null, DateTime? completedAt = null)
    {
        var profileId = Guid.NewGuid();
        var consultantId = Guid.NewGuid();
        var category = new ServiceCategory { Id = Guid.NewGuid(), Name = "Carreira " + first };
        var item = new ConsultantServiceItem { Id = Guid.NewGuid(), Name = "Revisão " + first, CategoryId = category.Id };
        db.AddRange(category, item);
        db.Profiles.Add(new Profile
        {
            Id = profileId, Email = first.ToLowerInvariant() + "@exemplo.com", Name = new PersonName { FirstName = first, LastName = "Costa" }, Role = RoleType.Consultant,
            BirthDate = new DateTime(1985, 5, 5, 0, 0, 0, DateTimeKind.Utc), DeletionRequestedAt = requestedAt, DeletionScheduledFor = requestedAt?.AddDays(7), DeletionCompletedAt = completedAt
        });
        var offering = new ConsultantServiceOffering { Id = Guid.NewGuid(), ConsultantId = consultantId, ServiceItemId = item.Id, PriceInLemonCoins = 150 };
        db.Consultants.Add(new Consultant
        {
            Id = consultantId, ProfileId = profileId, Name = new PersonName { FirstName = first, LastName = "Costa" }, Resume = new Resume { Summary = "Resumo de " + first },
            ProvidedServices = [offering],
            Availabilities = [new Availability { Id = Guid.NewGuid(), OwnerId = consultantId, WeekDay = "Monday", StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(10, 0), ServiceId = item.Id }]
        });
        await db.SaveChangesAsync();
        return new Seeded(profileId, consultantId, offering.Id);
    }

    private static readonly DateTime Now = new(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);

    [PostgresFact]
    public async Task Busca_match_e_existencia_mostram_so_quem_aceita_novos_talentos_e_cancelar_o_pedido_devolve()
    {
        var (setup, fresh, drop) = await CreateDatabaseAsync();
        try
        {
            var normal = await SeedAsync(setup, "Normal");
            var carencia = await SeedAsync(setup, "Carencia", requestedAt: Now.AddDays(-2));
            var excluido = await SeedAsync(setup, "Excluido", requestedAt: Now.AddDays(-9), completedAt: Now.AddDays(-1));
            var desistiu = await SeedAsync(setup, "Desistiu");                                         // pediu e cancelou: as colunas voltaram a nulo

            await using var read = fresh();
            var search = await new ConsultantServicesRepository(read).FilterConsultantsAsync("", "");
            var match = await new MatchRepository(read).ListConsultantsAsync(null);
            var matchByService = await new MatchRepository(read).ListConsultantsAsync("Revisão Carencia");

            Assert.Equal([normal.ConsultantId, desistiu.ConsultantId], search.Select(x => x.Id).OrderBy(x => x == normal.ConsultantId ? 0 : 1).ToArray());
            Assert.Equal(2, match.Count);
            Assert.DoesNotContain(match, x => x.Id == carencia.ConsultantId || x.Id == excluido.ConsultantId);
            Assert.Empty(matchByService);                                                              // nem procurando pelo serviço dele

            var consultants = new ConsultantServicesRepository(read);
            Assert.True(await consultants.AcceptsNewTalentsAsync(normal.ConsultantId));
            Assert.False(await consultants.AcceptsNewTalentsAsync(carencia.ConsultantId));
            Assert.False(await consultants.AcceptsNewTalentsAsync(excluido.ConsultantId));
            Assert.True(await consultants.AcceptsNewTalentsAsync(desistiu.ConsultantId));
            Assert.False(await consultants.AcceptsNewTalentsAsync(Guid.NewGuid()));                    // inexistente é como oculto

            // O pedido cancelado (pelo repositório do próprio fluxo) devolve o consultor à busca.
            await using (var db = fresh())
                await new ConsultantDeletionRepository(db).TryCancelAsync(carencia.ProfileId);
            await using var again = fresh();
            Assert.Contains((await new ConsultantServicesRepository(again).FilterConsultantsAsync("", "")), x => x.Id == carencia.ConsultantId);
        }
        finally
        {
            await drop();
        }
    }

    [PostgresFact]
    public async Task Os_fatos_do_convite_marcam_a_exclusao_e_o_que_ja_esta_em_andamento_segue()
    {
        var (setup, fresh, drop) = await CreateDatabaseAsync();
        try
        {
            var normal = await SeedAsync(setup, "Normal");
            var carencia = await SeedAsync(setup, "Carencia", requestedAt: Now.AddDays(-2));
            var talent = Guid.NewGuid();
            setup.Connections.Add(new Connection
            {
                Id = Guid.NewGuid(), ConsultantId = carencia.ConsultantId, CandidateId = talent,
                Candidate = new CandidateSnapshot { Name = new PersonName { FirstName = "Ana", LastName = "Lima" } }
            });
            await setup.SaveChangesAsync();

            await using var read = fresh();
            var invites = new InviteRepository(read);
            Assert.False((await invites.GetConsultantFactsAsync(normal.ConsultantId))!.LeavingAccount);
            Assert.True((await invites.GetConsultantFactsAsync(carencia.ConsultantId))!.LeavingAccount);

            // A conexão que já existia continua na lista do talento: o que está em andamento segue.
            var connections = await new ConsultantServicesRepository(read).GetConnectionsForCandidateAsync(talent);
            Assert.Equal(carencia.ConsultantId, Assert.Single(connections).ConsultantId);
        }
        finally
        {
            await drop();
        }
    }
}
