using core.domain.Entities;
using core.domain.Interfaces.Infrastructure;
using core.domain.Interfaces.Services;
using core.domain.Services.Conversations;
using core.domain.Services.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace core.Tests;

/// <summary>Os parâmetros da ponte para a equipe (docs/specs/ponte-talento-consultor.md, seção 39): o gRPC, a releitura e a troca da configuração em vigor.</summary>
public class SettingsGrpcTests
{
    private sealed class MemoryRepository : ISettingsRepository
    {
        public List<PlatformSetting> Settings { get; } = [];
        public List<PlatformSettingChange> Changes { get; } = [];
        public Exception? Throw { get; set; }
        public Task<List<PlatformSetting>> ListAsync() => Throw is not null ? throw Throw : Task.FromResult(Settings.ToList());
        public Task SaveAsync(PlatformSetting setting, PlatformSettingChange change)
        {
            Settings.RemoveAll(x => x.Key == setting.Key);
            Settings.Add(setting);
            Changes.Add(change);
            return Task.CompletedTask;
        }
        public Task RemoveAsync(string key, PlatformSettingChange change)
        {
            Settings.RemoveAll(x => x.Key == key);
            Changes.Add(change);
            return Task.CompletedTask;
        }
        public Task<List<PlatformSettingChange>> ListChangesAsync(int take) => Task.FromResult(Changes.OrderByDescending(x => x.ChangedAt).Take(take).ToList());
    }

    private readonly MemoryRepository _repository = new();
    private readonly ConversationConfigurationHolder _holder = new();
    private readonly core.Services.SettingsGrpcService _grpc;

    public SettingsGrpcTests()
        => _grpc = new core.Services.SettingsGrpcService(new SettingsService(_repository, _holder, new FakeTimeProvider(new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero))),
            NullLogger<core.Services.SettingsGrpcService>.Instance);

    private static Grpc.Core.ServerCallContext Context => TestServerCallContext.Create();

    [Fact]
    public async Task Listar_devolve_todos_os_parametros_com_padrao_faixa_e_valor_em_vigor()
    {
        var reply = await _grpc.ListSettings(new SetEmptyRequest(), Context);

        Assert.Equal(200, reply.Statuscode);
        var items = reply.Data.Unpack<SetListReply>().Items;
        Assert.Equal(ConversationSettings.Definitions.Count, items.Count);
        var quota = items.Single(i => i.Key == "InviteMonthlyQuota");
        Assert.Equal((10, 10, 1, 50, false, ""), (quota.Default, quota.Value, quota.Min, quota.Max, quota.IsCustom, quota.UpdatedBy));
        Assert.False(string.IsNullOrWhiteSpace(quota.Label));
        Assert.False(string.IsNullOrWhiteSpace(quota.Group));
    }

    [Fact]
    public async Task Mudar_devolve_o_parametro_atualizado_com_quem_mudou_e_a_data_em_ISO()
    {
        var reply = await _grpc.UpdateSetting(new SetUpdateRequest { Key = "InviteMonthlyQuota", Value = 15, Actor = "ana@exemplo.com", Note = "Calibragem" }, Context);

        Assert.Equal(200, reply.Statuscode);
        var item = reply.Data.Unpack<SetItem>();
        Assert.Equal((15, true, "ana@exemplo.com"), (item.Value, item.IsCustom, item.UpdatedBy));
        Assert.Equal("2026-11-17T12:00:00.0000000Z", item.UpdatedAt);
        Assert.Equal(15, _holder.Current.InviteMonthlyQuota);
    }

    [Theory]
    [InlineData("InviteMonthlyQuota", 0, "ana")]              // fora da faixa
    [InlineData("InviteMonthlyQuotaNew", 11, "ana")]          // passa do convite por mês
    [InlineData("ReportRetentionDays", 30, "ana")]            // não é ajustável
    [InlineData("InviteMonthlyQuota", 15, "")]                // sem quem mudou
    public async Task Erro_de_regra_vira_400_com_a_mensagem_para_a_equipe_e_nada_muda(string key, int value, string actor)
    {
        var reply = await _grpc.UpdateSetting(new SetUpdateRequest { Key = key, Value = value, Actor = actor }, Context);

        Assert.Equal(400, reply.Statuscode);
        Assert.False(string.IsNullOrWhiteSpace(reply.Error.Message));
        Assert.Empty(_repository.Changes);
        Assert.Equal(10, _holder.Current.InviteMonthlyQuota);
    }

    [Fact]
    public async Task Voltar_ao_padrao_devolve_o_parametro_sem_a_marca_de_mudado()
    {
        await _grpc.UpdateSetting(new SetUpdateRequest { Key = "AreasMax", Value = 4, Actor = "ana" }, Context);

        var reply = await _grpc.ResetSetting(new SetResetRequest { Key = "AreasMax", Actor = "bia", Note = "Voltando" }, Context);

        Assert.Equal(200, reply.Statuscode);
        var item = reply.Data.Unpack<SetItem>();
        Assert.Equal((3, false), (item.Value, item.IsCustom));
        Assert.Equal(3, _holder.Current.AreasMax);
    }

    [Fact]
    public async Task O_historico_marca_quando_estava_no_padrao_ou_voltou_ao_padrao()
    {
        await _grpc.UpdateSetting(new SetUpdateRequest { Key = "AreasMax", Value = 4, Actor = "ana" }, Context);
        await _grpc.ResetSetting(new SetResetRequest { Key = "AreasMax", Actor = "bia" }, Context);

        var items = (await _grpc.ListChanges(new SetChangesRequest { Take = 10 }, Context)).Data.Unpack<SetChangesReply>().Items;

        Assert.Equal(2, items.Count);
        var reset = items.Single(i => i.ChangedBy == "bia");
        Assert.Equal((true, 4, false), (reset.HasOldValue, reset.OldValue, reset.HasNewValue));
        var first = items.Single(i => i.ChangedBy == "ana");
        Assert.Equal((false, true, 4), (first.HasOldValue, first.HasNewValue, first.NewValue));
        Assert.Equal("Áreas no pedido", first.Label);
    }

    [Fact]
    public async Task Falha_inesperada_vira_500_e_nao_derruba_o_servico()
    {
        _repository.Throw = new InvalidOperationException("banco fora do ar");

        var reply = await _grpc.ListSettings(new SetEmptyRequest(), Context);

        Assert.Equal(500, reply.Statuscode);
        Assert.DoesNotContain("banco", reply.Error.Message);         // o texto técnico não chega à tela da equipe
    }

    // ---- a configuração em vigor chega a quem a usa ------------------------------------------------------------------------------

    [Fact]
    public async Task Servico_criado_depois_da_mudanca_recebe_a_configuracao_nova_e_o_ja_criado_nao_muda_no_meio_de_uma_operacao()
    {
        // O mesmo registro do Program.cs: UMA instância guarda a configuração e cada Transient recebe a que está em vigor quando é criado.
        var services = new ServiceCollection();
        services.AddSingleton<ConversationConfigurationHolder>();
        services.AddTransient(sp => sp.GetRequiredService<ConversationConfigurationHolder>().Current);
        using var provider = services.BuildServiceProvider();
        var holder = provider.GetRequiredService<ConversationConfigurationHolder>();

        var before = provider.GetRequiredService<ConversationConfiguration>();
        holder.Replace(new ConversationConfiguration { InviteMonthlyQuota = 25 });
        var after = provider.GetRequiredService<ConversationConfiguration>();

        Assert.Equal(10, before.InviteMonthlyQuota);       // quem já estava trabalhando termina com os valores com que começou
        Assert.Equal(25, after.InviteMonthlyQuota);
    }

    // ---- a releitura --------------------------------------------------------------------------------------------------------------

    private (core.IoC.SettingsRefreshJob Job, ConversationConfigurationHolder Holder) Job()
    {
        var holder = new ConversationConfigurationHolder();
        var services = new ServiceCollection()
            .AddSingleton<ISettingsRepository>(_repository)
            .AddSingleton(holder)
            .AddSingleton<TimeProvider>(new FakeTimeProvider())
            .AddTransient<ISettingsService, SettingsService>()
            .BuildServiceProvider();
        return (new core.IoC.SettingsRefreshJob(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<core.IoC.SettingsRefreshJob>.Instance), holder);
    }

    [Fact]
    public async Task A_releitura_traz_o_que_outro_processo_gravou()
    {
        _repository.Settings.Add(new PlatformSetting { Key = "InviteExpiryDays", Value = 30, UpdatedAt = DateTime.UtcNow, UpdatedBy = "outro-pod" });
        var (job, holder) = Job();

        Assert.True(await job.RefreshAsync(default));

        Assert.Equal(30, holder.Current.InviteExpiryDays);
    }

    [Fact]
    public async Task Falha_na_releitura_nao_derruba_e_mantem_a_ultima_configuracao_lida()
    {
        var (job, holder) = Job();
        _repository.Settings.Add(new PlatformSetting { Key = "InviteExpiryDays", Value = 30, UpdatedAt = DateTime.UtcNow, UpdatedBy = "x" });
        await job.RefreshAsync(default);

        _repository.Throw = new IOException("banco fora do ar");

        Assert.False(await job.RefreshAsync(default));        // não lança
        Assert.Equal(30, holder.Current.InviteExpiryDays);    // segue valendo a última que deu certo
    }

    [Fact]
    public async Task Ao_subir_com_o_banco_fora_do_ar_o_processo_segue_com_os_padroes_do_codigo()
    {
        _repository.Throw = new IOException("banco fora do ar");
        var (job, holder) = Job();

        await job.StartAsync(default);
        await job.StopAsync(default);

        Assert.Equal(10, holder.Current.InviteMonthlyQuota);
    }

    [Fact]
    public async Task O_cancelamento_nao_vira_erro()
    {
        var (job, _) = Job();
        _repository.Throw = new OperationCanceledException();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.False(await job.RefreshAsync(cts.Token));
    }
}
