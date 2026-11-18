using core.domain.Interfaces.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace core.Tests;

/// <summary>A medição da ponte para a equipe (docs/specs/ponte-talento-consultor.md, seção 40): o gRPC só traduz o resultado agregado, e o contrato não tem como identificar ninguém.</summary>
public class PonteMetricsGrpcTests
{
    private sealed class FakeMetrics : IPonteMetricsService
    {
        public int? AskedDays { get; private set; }
        public Exception? Throw { get; set; }
        public PonteMetrics Result { get; set; } = Sample();
        public Task<PonteMetrics> GetAsync(int days)
        {
            AskedDays = days;
            return Throw is not null ? throw Throw : Task.FromResult(Result);
        }
    }

    private static PonteMetrics Sample(int? median = 50, int? p90 = 90) => new(
        new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc), 30, false,
        new HandRaiseFunnel(35, 14, 4, 5, 6, 7, 8, 5), new FirstConversationFunnel(11, 5), new InviteFunnel(21, 2, 3, 4, 5, 6, 1),
        new ReportCounts(5, 4, 6, 8, 7, 2, 1, 3),
        new ResponseTimeMetrics(10, 4, median, p90, [new("lt1h", 1), new("1to6h", 2), new("6to24h", 3), new("1to3d", 2), new("3to7d", 1), new("gt7d", 1)]));

    private readonly FakeMetrics _metrics = new();
    private readonly core.Services.PonteMetricsGrpcService _grpc;

    public PonteMetricsGrpcTests() => _grpc = new core.Services.PonteMetricsGrpcService(_metrics, NullLogger<core.Services.PonteMetricsGrpcService>.Instance);

    [Fact]
    public async Task Repassa_o_periodo_e_traduz_todos_os_funis_a_moderacao_e_o_tempo_de_resposta()
    {
        var reply = await _grpc.GetMetrics(new PmMetricsRequest { Days = 7 }, TestServerCallContext.Create());

        Assert.Equal(200, reply.Statuscode);
        Assert.Equal(7, _metrics.AskedDays);
        var m = reply.Data.Unpack<PmMetricsReply>();
        Assert.Equal(("2026-10-19T12:00:00.0000000Z", "2026-11-18T12:00:00.0000000Z", 30, false), (m.From, m.To, m.Days, m.Truncated));
        Assert.Equal((35, 14, 4, 5, 6, 7, 8, 5), (m.HandRaise.Raised, m.HandRaise.WithProposal, m.HandRaise.Confirmed, m.HandRaise.Declined, m.HandRaise.ExpiredNoResponse, m.HandRaise.ExpiredAfterProposal, m.HandRaise.Withdrawn, m.HandRaise.Open));
        Assert.Equal((11, 5), (m.FirstConversation.Proposed, m.FirstConversation.Confirmed));
        Assert.Equal((21, 2, 3, 4, 5, 6, 1), (m.Invites.Sent, m.Invites.Accepted, m.Invites.Dismissed, m.Invites.Silenced, m.Invites.Expired, m.Invites.Held, m.Invites.Open));
        Assert.Equal((5, 4, 6, 8, 7, 2, 1, 3), (m.Reports.InviteByTalent, m.Reports.HandRaiseByTalent, m.Reports.ByConsultant, m.Reports.Open, m.Reports.Reviewed, m.Reports.SuspensionsAuto, m.Reports.SuspensionsManual, m.Reports.SuspensionsLifted));
        Assert.Equal((10, 4, true, 50, true, 90), (m.ResponseTime.Measured, m.ResponseTime.NoData, m.ResponseTime.HasMedian, m.ResponseTime.MedianMinutes, m.ResponseTime.HasP90, m.ResponseTime.P90Minutes));
        Assert.Equal(["lt1h", "1to6h", "6to24h", "1to3d", "3to7d", "gt7d"], m.ResponseTime.Buckets.Select(b => b.Key));
        Assert.Equal([1, 2, 3, 2, 1, 1], m.ResponseTime.Buckets.Select(b => b.Count));
    }

    [Fact]
    public async Task Sem_resposta_nenhuma_a_mediana_e_o_p90_chegam_como_sem_dado_e_nao_como_zero_minutos()
    {
        _metrics.Result = Sample(median: null, p90: null);

        var m = (await _grpc.GetMetrics(new PmMetricsRequest(), TestServerCallContext.Create())).Data.Unpack<PmMetricsReply>();

        Assert.False(m.ResponseTime.HasMedian);
        Assert.False(m.ResponseTime.HasP90);
    }

    [Fact]
    public async Task A_coorte_cortada_pelo_teto_chega_avisada()
    {
        _metrics.Result = Sample() with { Truncated = true };

        Assert.True((await _grpc.GetMetrics(new PmMetricsRequest(), TestServerCallContext.Create())).Data.Unpack<PmMetricsReply>().Truncated);
    }

    [Fact]
    public async Task Falha_inesperada_vira_500_com_mensagem_generica_sem_o_texto_tecnico()
    {
        _metrics.Throw = new InvalidOperationException("senha do banco errada em db-prod-01");

        var reply = await _grpc.GetMetrics(new PmMetricsRequest(), TestServerCallContext.Create());

        Assert.Equal(500, reply.Statuscode);
        Assert.DoesNotContain("senha", reply.Error.Message);
        Assert.DoesNotContain("db-prod", reply.Error.Message);
    }

    [Fact]
    public void O_contrato_nao_tem_nenhum_campo_que_identifique_talento_consultor_ou_conversa()
    {
        var messages = new[]
        {
            PmMetricsRequest.Descriptor, PmMetricsReply.Descriptor, PmHandRaiseFunnel.Descriptor, PmFirstConversation.Descriptor, PmInviteFunnel.Descriptor, PmReports.Descriptor,
            PmResponseTime.Descriptor, PmBucket.Descriptor
        };
        var fields = messages.SelectMany(m => m.Fields.InFieldNumberOrder()).ToList();

        Assert.DoesNotContain(fields, f => f.FieldType == Google.Protobuf.Reflection.FieldType.Bytes);
        Assert.DoesNotContain(fields, f => f.Name.ToLowerInvariant() is var n && (n.Contains("email") || n.Contains("name") || n.Contains("phone") || n.Contains("candidate") || n.EndsWith("id")));
    }
}
