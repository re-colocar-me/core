using core.domain.Entities;
using core.domain.Enums;
using core.domain.Services;
using core.infrastructure;
using core.infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace core.Tests;

using Consultant = core.domain.Entities.Consultant;

/// <summary>
/// A situação que a tela de Serviços mostra e o bloqueio da oferta são a mesma regra (docs/specs/curadoria-skills-consultor.md): em Postgres real, para <b>cada</b> serviço do catálogo, "liberado" na
/// consulta é exatamente "o servidor aceita a oferta".
/// </summary>
public class CurationPostgresTests
{
    private static async Task<(Func<ReColocarmeContext> Fresh, Func<Task> Drop)> CreateDatabaseAsync()
    {
        var admin = Environment.GetEnvironmentVariable(PostgresFactAttribute.Variable)!;
        var name = "core_curation_" + Guid.NewGuid().ToString("N")[..10];
        await using (var connection = new NpgsqlConnection(admin + ";Database=postgres"))
        {
            await connection.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await create.ExecuteNonQueryAsync();
        }

        ReColocarmeContext Make() => new(new DbContextOptionsBuilder<ReColocarmeContext>().UseNpgsql(admin + ";Database=" + name).Options);
        await using (var migrate = Make())
            await migrate.Database.MigrateAsync();

        return (Make, async () =>
        {
            NpgsqlConnection.ClearAllPools();
            await using var connection = new NpgsqlConnection(admin + ";Database=postgres");
            await connection.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", connection);
            await drop.ExecuteNonQueryAsync();
        });
    }

    [PostgresFact]
    public async Task Liberado_na_consulta_e_exatamente_o_que_o_servidor_aceita_ofertar()
    {
        var (fresh, drop) = await CreateDatabaseAsync();
        try
        {
            var owner = Guid.NewGuid();
            var seo = new Skill { Id = Guid.NewGuid(), Name = "SEO" };
            var listening = new Skill { Id = Guid.NewGuid(), Name = "Escuta Ativa" };
            var writing = new Skill { Id = Guid.NewGuid(), Name = "Copywriting" };
            var interview = new Skill { Id = Guid.NewGuid(), Name = "Entrevista Técnica" };

            // A categoria pede Escuta Ativa de todos os seus serviços.
            var category = new ServiceCategory { Id = Guid.NewGuid(), Name = "Currículo e LinkedIn", Skills = [listening] };
            var half = new ConsultantServiceItem { Id = Guid.NewGuid(), Name = "Metade", CategoryId = category.Id, Skills = [writing] };            // 1 de 2
            var blocked = new ConsultantServiceItem { Id = Guid.NewGuid(), Name = "Pouco", CategoryId = category.Id, Skills = [writing, interview] }; // 1 de 3
            var full = new ConsultantServiceItem { Id = Guid.NewGuid(), Name = "Tudo", CategoryId = category.Id, Skills = [seo, listening] };        // 2 de 2 (a repetida conta uma vez)
            var empty = new ServiceCategory { Id = Guid.NewGuid(), Name = "Sem skills" };
            var uncurated = new ConsultantServiceItem { Id = Guid.NewGuid(), Name = "Nada cadastrado", CategoryId = empty.Id };

            await using (var seed = fresh())
            {
                seed.AddRange(category, empty, half, blocked, full, uncurated);
                seed.Consultants.Add(new Consultant { Id = Guid.NewGuid(), ProfileId = owner, Name = new PersonName { FirstName = "Ana", LastName = "Teste" } });
                await seed.SaveChangesAsync();
            }

            // As skills do consultor, com outra grafia de maiúsculas, pelo mesmo caminho da edição manual.
            await using (var skills = fresh())
                await new ConsultantServicesRepository(skills).SetConsultantSkillsAsync(owner, ["escuta ativa", "seo"]);

            await using var read = fresh();
            var eligibility = (await new ConsultantServiceServices(new ConsultantServicesRepository(read)).GetServiceEligibility(owner)).ToDictionary(x => x.ServiceId);

            Assert.True(eligibility[half.Id].Eligible);
            Assert.Equal(["Escuta Ativa"], eligibility[half.Id].Match.Matched);
            Assert.Equal(["Copywriting"], eligibility[half.Id].Match.Missing);
            Assert.False(eligibility[blocked.Id].Eligible);
            Assert.Equal(["Copywriting", "Entrevista Técnica"], eligibility[blocked.Id].Match.Missing);
            Assert.True(eligibility[full.Id].Eligible);
            Assert.Equal(2, eligibility[full.Id].Match.Required);
            Assert.False(eligibility[uncurated.Id].Eligible);
            Assert.Equal(0, eligibility[uncurated.Id].Match.Required);
            Assert.All(eligibility.Values, x => Assert.Equal(50m, x.MinimumPercentage));

            foreach (var item in new[] { half, blocked, full, uncurated })
            {
                await using var db = fresh();
                var error = await Record.ExceptionAsync(() => new ConsultantServicesRepository(db).SetServiceAsync(item.Id, owner, ServiceFormat.HourClass, null));
                Assert.True(eligibility[item.Id].Eligible == (error is null), $"{item.Name}: a consulta disse {eligibility[item.Id].Eligible} e a oferta respondeu '{error?.Message}'");
                if (item == blocked)
                    Assert.Equal("Você atende a 1 de 3 skills exigidas (33%) — mínimo necessário: 50%.", error!.Message);
            }

            // O mínimo mudou no backoffice: a consulta e o bloqueio mudam juntos.
            await using (var config = fresh())
                await new ConsultantServicesRepository(config).SetMinimumSkillMatchPercentageAsync(30m);
            await using var after = fresh();
            var again = (await new ConsultantServiceServices(new ConsultantServicesRepository(after)).GetServiceEligibility(owner)).ToDictionary(x => x.ServiceId);
            Assert.True(again[blocked.Id].Eligible);
            await using var offer = fresh();
            await new ConsultantServicesRepository(offer).SetServiceAsync(blocked.Id, owner, ServiceFormat.HourClass, null);
            Assert.Equal(3, await offer.ConsultantServiceOfferings.CountAsync());
        }
        finally { await drop(); }
    }
}
