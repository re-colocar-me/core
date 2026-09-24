import json, os

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..')
os.makedirs(OUT, exist_ok=True)

IDX_REF = {"name": "kibanaSavedObjectMeta.searchSourceJSON.index", "type": "index-pattern", "id": "backup-status"}


def search_source(query=""):
    return json.dumps({
        "query": {"query": query, "language": "kuery"},
        "filter": [],
        "indexRefName": "kibanaSavedObjectMeta.searchSourceJSON.index",
    }, ensure_ascii=False)


def table_vis(title, aggs, query=""):
    vis_state = {
        "title": title,
        "type": "table",
        "aggs": aggs,
        "params": {
            "perPage": 10,
            "showPartialRows": False,
            "showMetricsAtAllLevels": False,
            "showToolbar": False,
            "showTotal": False,
            "totalFunc": "sum",
            "percentageCol": "",
            "autoFitRowToContent": False,
        },
    }
    return {
        "title": title, "visState": json.dumps(vis_state, ensure_ascii=False), "uiStateJSON": "{}",
        "description": "", "version": 1,
        "kibanaSavedObjectMeta": {"searchSourceJSON": search_source(query)},
    }


def terms(agg_id, field, label, order_by="_key", order="asc", size=5):
    return {"id": agg_id, "enabled": True, "type": "terms", "schema": "bucket", "params": {
        "field": field, "orderBy": order_by, "order": order, "size": size, "otherBucket": False,
        "otherBucketLabel": "Outros", "missingBucket": False, "missingBucketLabel": "Sem valor", "customLabel": label}}


objects = []

# 1) Último backup OK por alvo (tabela)
last_ok = table_vis(
    "Último backup OK por alvo",
    [
        {"id": "1", "enabled": True, "type": "max", "schema": "metric",
         "params": {"field": "@timestamp", "customLabel": "Último backup OK"}},
        terms("2", "target.keyword", "Alvo"),
    ],
    query='status : "ok"',
)
objects.append({"type": "visualization", "id": "backup-vis-last-ok", "attributes": last_ok, "references": [IDX_REF]})

# 2) Backups por dia, alvo e resultado (tabela)
per_day = table_vis(
    "Backups por dia",
    [
        {"id": "1", "enabled": True, "type": "count", "schema": "metric", "params": {"customLabel": "Execuções"}},
        {"id": "2", "enabled": True, "type": "date_histogram", "schema": "bucket", "params": {
            "field": "@timestamp", "useNormalizedEsInterval": True, "scaleMetricValues": False, "interval": "1d",
            "drop_partials": False, "min_doc_count": 1, "extended_bounds": {}, "customLabel": "Dia"}},
        terms("3", "target.keyword", "Alvo"),
        terms("4", "status.keyword", "Resultado"),
    ],
)
objects.append({"type": "visualization", "id": "backup-vis-per-day", "attributes": per_day, "references": [IDX_REF]})

# 3) Falhas no período (métrica)
failures_state = {
    "title": "Falhas no período",
    "type": "metric",
    "aggs": [{"id": "1", "enabled": True, "type": "count", "schema": "metric", "params": {"customLabel": "Falhas no período"}}],
    "params": {"addTooltip": True, "addLegend": False, "type": "metric", "metric": {
        "percentageMode": False, "useRanges": True, "colorSchema": "Green to Red", "metricColorMode": "Background",
        "colorsRange": [{"from": 0, "to": 1}, {"from": 1, "to": 1000000}], "labels": {"show": True},
        "invertColors": False, "style": {"bgFill": "#000", "bgColor": False, "labelColor": False, "subText": "", "fontSize": 60}}},
}
objects.append({"type": "visualization", "id": "backup-vis-failures", "attributes": {
    "title": "Falhas no período", "visState": json.dumps(failures_state, ensure_ascii=False), "uiStateJSON": "{}",
    "description": "", "version": 1,
    "kibanaSavedObjectMeta": {"searchSourceJSON": search_source('status : "failed"')}}, "references": [IDX_REF]})

# 4) Texto de ajuda (markdown)
help_md = (
    "### Backups: como ler este painel\n\n"
    "- **Esperado**: um backup `ok` de cada alvo a cada **6 horas**: Postgres às :30 e Elasticsearch às :50 "
    "(UTC) das 00, 06, 12 e 18 h.\n"
    "- **Alerta manual**: se o *Último backup OK* de algum alvo tiver **mais de ~7 horas**, ou houver "
    "*Falhas no período* acima de zero, investigar.\n"
    "- Se um alvo **não aparece** na tabela, não há nenhum backup `ok` dele no período selecionado "
    "(ajuste o intervalo de tempo).\n"
    "- Falhas que acontecem antes do envio (por exemplo no `dump`) não gravam status aqui: conferir "
    "`kubectl get jobs -n consultor` e `kubectl logs job/<nome> -n consultor --all-containers`.\n"
    "- Restaurações: `docs/runbooks/restauracao-postgres-e-elasticsearch.md`."
)
help_state = {"title": "Como ler", "type": "markdown", "aggs": [], "params": {"fontSize": 12, "openLinksInNewTab": False, "markdown": help_md}}
objects.append({"type": "visualization", "id": "backup-vis-help", "attributes": {
    "title": "Como ler este painel", "visState": json.dumps(help_state, ensure_ascii=False), "uiStateJSON": "{}",
    "description": "", "version": 1,
    "kibanaSavedObjectMeta": {"searchSourceJSON": json.dumps({"query": {"query": "", "language": "kuery"}, "filter": []})}},
    "references": []})

# 5) Busca salva: últimas execuções
objects.append({"type": "search", "id": "backup-search-recent", "attributes": {
    "title": "Últimas execuções de backup", "description": "", "hits": 0,
    "columns": ["target", "status", "files", "bytes", "indices", "docs", "encrypted", "duration_seconds", "prefix"],
    "sort": [["@timestamp", "desc"]], "version": 1,
    "kibanaSavedObjectMeta": {"searchSourceJSON": search_source("")}}, "references": [IDX_REF]})

# 6) Dashboard
panels = [
    ("backup-vis-help",      "visualization", 0,  0, 24, 10),
    ("backup-vis-failures",  "visualization", 24, 0, 24, 10),
    ("backup-vis-last-ok",   "visualization", 0,  10, 24, 12),
    ("backup-vis-per-day",   "visualization", 24, 10, 24, 12),
    ("backup-search-recent", "search",        0,  22, 48, 16),
]
panels_json, refs = [], []
for n, (pid, ptype, x, y, w, h) in enumerate(panels):
    panels_json.append({
        "version": "8.17.6", "type": ptype,
        "gridData": {"x": x, "y": y, "w": w, "h": h, "i": str(n)},
        "panelIndex": str(n), "embeddableConfig": {}, "panelRefName": f"panel_{n}"})
    refs.append({"name": f"panel_{n}", "type": ptype, "id": pid})
objects.append({"type": "dashboard", "id": "backup-status-dashboard", "attributes": {
    "title": "Backups: estado", "description": "Estado dos backups do Postgres e do Elasticsearch (índice backup-status).",
    "panelsJSON": json.dumps(panels_json), "optionsJSON": json.dumps({"useMargins": True, "syncColors": False, "syncCursor": True, "syncTooltips": False, "hidePanelTitles": False}),
    "timeRestore": True, "timeFrom": "now-7d", "timeTo": "now",
    "refreshInterval": {"pause": False, "value": 60000}, "version": 1,
    "kibanaSavedObjectMeta": {"searchSourceJSON": json.dumps({"query": {"query": "", "language": "kuery"}, "filter": []})}},
    "references": refs})

with open(os.path.join(OUT, 'backup-status-dashboard.json'), 'w', encoding='utf-8') as f:
    json.dump(objects, f, ensure_ascii=False, indent=2)

data_view = {"data_view": {"id": "backup-status", "title": "backup-status", "name": "Backups (status)", "timeFieldName": "@timestamp"}, "override": True}
with open(os.path.join(OUT, 'backup-status-dataview.json'), 'w', encoding='utf-8') as f:
    json.dump(data_view, f, ensure_ascii=False, indent=2)
print('objetos:', [(o['type'], o['id']) for o in objects])
