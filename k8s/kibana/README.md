# Painéis do Kibana (definições versionadas)

Painéis criados em 2026-09-24 por API, a partir de geradores em `generators/` (o Kibana 8.17.6 do cluster não tinha nenhum painel nosso). Os `.json` desta pasta são a **saída dos geradores**: edite o gerador e regenere, não edite o JSON à mão.

| Painel | ID | O que mostra | Fonte |
|---|---|---|---|
| **Backups: estado** | `backup-status-dashboard` | último backup OK por alvo, falhas, execuções por dia | índice `backup-status` |
| **Erros e saúde** | `observabilidade-erros` | erros e fatais, 5xx, falhas de fila, endpoints lentos, gRPC com falha, rotas 404 e scanners | `logs-*-production` |
| **Uso da plataforma** | `observabilidade-uso` | logins, currículos, teste vocacional (com funil), ferramentas de IA, carteira, perfis ativos, auditoria | logs de request dos BFFs e `audit-*` |

URL de qualquer painel: `https://kibana-re.colocar-me.com.br/app/dashboards#/view/<ID>` (Basic Auth do Traefik).

## Data views criados

| ID | Índices | Observação |
|---|---|---|
| `backup-status` | `backup-status` | |
| `logs-all` | `logs-*` | erros (nível `Error`/`Fatal`) |
| `logs-http` | logs dos BFFs e do `core_subscribers` | `StatusCode` é **numérico**. Campos calculados: `endpoint` (método + caminho com GUID trocado por `{id}`) e `owner_id` (primeiro GUID de rotas do próprio usuário) |
| `logs-grpc` | logs de `core`, `core-wallet`, `core-vocacional`, `core-backoffice`, `core-promocodes` | `StatusCode` é **texto** (`OK`, ...) |
| `audit-all` | `audit-*` | |

Os logs HTTP e gRPC ficam em data views separados porque `metadata.LogDetails.StatusCode` tem tipos diferentes (número num, texto no outro).

## Reimportar (por exemplo depois de recriar o Kibana)

De um lugar com acesso ao cluster, na raiz do repo `core`:

```
KB="kubectl exec -i -n consultor elasticsearch-0 -c elasticsearch -- curl -s -H kbn-xsrf:true -H Content-Type:application/json"

# 1) data views (um por linha; `override` recria o que já existir)
python3 -c "import json; [print(json.dumps(d)) for d in json.load(open('k8s/kibana/observability-dataviews.json', encoding='utf-8'))]" > /tmp/dvs.txt
$KB -X POST http://kibana:5601/api/data_views/data_view -d @- < k8s/kibana/backup-status-dataview.json
while IFS= read -r l; do printf '%s' "$l" | $KB -X POST http://kibana:5601/api/data_views/data_view -d @-; done < /tmp/dvs.txt

# 2) objetos (visualizações, buscas salvas, painéis)
$KB -X POST "http://kibana:5601/api/saved_objects/_bulk_create?overwrite=true" -d @- < k8s/kibana/backup-status-dashboard.json
$KB -X POST "http://kibana:5601/api/saved_objects/_bulk_create?overwrite=true" -d @- < k8s/kibana/observability-dashboards.json
```

Regenerar os JSON: `python3 k8s/kibana/generators/gen_observability_dashboards.py` (e `gen_backup_dashboard.py`).

## Armadilhas já encontradas

- **O import do Kibana não valida scripts nem KQL.** Um erro de compilação no campo calculado só aparece quando o painel roda. Em 2026-09-24 o script do `endpoint` não compilava (`+ /regex/` não é aceito em Painless; o regex tem que ir para uma variável antes). Sempre teste o script no Elasticsearch com `runtime_mappings` antes de importar.
- **Painéis validados só por API**, nunca visualmente por quem escreveu; a primeira pessoa a abri-los deve avisar se algum painel está em branco.
- Os painéis de "Uso da plataforma" medem **atividade** (requisições 2xx com método e caminho conhecidos), não faturamento nem cadastros oficiais; retentativas e cliques repetidos contam mais de uma vez. Um recurso novo só aparece se o método e o caminho dele forem acrescentados à lista de eventos do gerador.
- Não enviam alerta; são só visualização.
