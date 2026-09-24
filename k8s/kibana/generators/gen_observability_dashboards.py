import json, os

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..')
os.makedirs(OUT, exist_ok=True)

GUID = r"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}"

HTTP_STREAMS = ",".join(f"logs-{s}-production" for s in
                        ["consultant-bff", "talent-bff", "backofficebff", "vocacionalbff", "subscribers"])
GRPC_STREAMS = ",".join(f"logs-{s}-production" for s in
                        ["core", "wallet", "vocacional", "backoffice", "promocodes"])

# ---------------------------------------------------------------- data views
data_views = [
    {"data_view": {"id": "logs-all", "title": "logs-*", "name": "Logs (todos os serviços)", "timeFieldName": "@timestamp"}, "override": True},
    {"data_view": {"id": "logs-http", "title": HTTP_STREAMS, "name": "Logs HTTP (BFFs e subscribers)", "timeFieldName": "@timestamp",
                   "runtimeFieldMap": {
                       # Endpoint normalizado: método + caminho com os GUIDs trocados por {id}.
                       "endpoint": {"type": "keyword", "script": {"source":
                           "def p = doc['metadata.LogDetails.RequestPath.Value']; def m = doc['metadata.LogDetails.RequestMethod']; "
                           "if (p.size() == 0 || m.size() == 0) return; "
                           # O regex precisa ir para uma variável: `+ /regex/` não compila em Painless.
                           "def norm = /" + GUID + "/.matcher(p.value).replaceAll('{id}'); "
                           "emit(m.value + ' ' + norm);"}},
                       # Dono do recurso: primeiro GUID de caminhos que começam por uma rota do próprio usuário.
                       "owner_id": {"type": "keyword", "script": {"source":
                           "def p = doc['metadata.LogDetails.RequestPath.Value']; if (p.size() == 0) return; def v = p.value; "
                           "if (!(v.startsWith('/profile/') || v.startsWith('/notification/') || v.startsWith('/wallet/') || v.startsWith('/schedule/') "
                           "|| v.startsWith('/tratativas/') || v.startsWith('/connections/') || v.startsWith('/resume/') || v.startsWith('/consultant/') "
                           "|| v.startsWith('/services/consultant/'))) return; "
                           "def m = /" + GUID + "/.matcher(v); if (m.find()) { emit(m.group()); }"}},
                   }}, "override": True},
    {"data_view": {"id": "logs-grpc", "title": GRPC_STREAMS, "name": "Logs gRPC (core-*)", "timeFieldName": "@timestamp"}, "override": True},
    {"data_view": {"id": "audit-all", "title": "audit-*", "name": "Auditoria", "timeFieldName": "@timestamp"}, "override": True},
]

objects = []


def ref(dv):
    return [{"name": "kibanaSavedObjectMeta.searchSourceJSON.index", "type": "index-pattern", "id": dv}]


def src(query=""):
    return json.dumps({"query": {"query": query, "language": "kuery"}, "filter": [],
                       "indexRefName": "kibanaSavedObjectMeta.searchSourceJSON.index"}, ensure_ascii=False)


def add_vis(vid, title, dv, vis_state, query="", description=""):
    vis_state["title"] = title
    objects.append({"type": "visualization", "id": vid, "attributes": {
        "title": title, "visState": json.dumps(vis_state, ensure_ascii=False), "uiStateJSON": "{}",
        "description": description, "version": 1,
        "kibanaSavedObjectMeta": {"searchSourceJSON": src(query)}}, "references": ref(dv)})


def terms(i, field, label, order_by="_key", order="asc", size=10, schema="bucket"):
    return {"id": i, "enabled": True, "type": "terms", "schema": schema, "params": {
        "field": field, "orderBy": order_by, "order": order, "size": size, "otherBucket": False,
        "otherBucketLabel": "Outros", "missingBucket": False, "missingBucketLabel": "Sem valor", "customLabel": label}}


def metric(i, typ, label, field=None):
    p = {"customLabel": label}
    if field:
        p["field"] = field
    return {"id": i, "enabled": True, "type": typ, "schema": "metric", "params": p}


def filters_agg(i, filters, label="Evento", schema="bucket"):
    return {"id": i, "enabled": True, "type": "filters", "schema": schema, "params": {
        "filters": [{"input": {"query": q, "language": "kuery"}, "label": l} for l, q in filters], "customLabel": label}}


def date_hist(i, label="Data", schema="segment"):
    return {"id": i, "enabled": True, "type": "date_histogram", "schema": schema, "params": {
        "field": "@timestamp", "useNormalizedEsInterval": True, "scaleMetricValues": False, "interval": "auto",
        "drop_partials": False, "min_doc_count": 0, "extended_bounds": {}, "customLabel": label}}


def table(vid, title, dv, aggs, query="", per_page=10, description=""):
    add_vis(vid, title, dv, {"type": "table", "aggs": aggs, "params": {
        "perPage": per_page, "showPartialRows": False, "showMetricsAtAllLevels": False, "showToolbar": False,
        "showTotal": False, "totalFunc": "sum", "percentageCol": "", "autoFitRowToContent": False}}, query, description)


def big_number(vid, title, dv, query, bad_above_zero=True):
    ranges = [{"from": 0, "to": 1}, {"from": 1, "to": 100000000}] if bad_above_zero else [{"from": 0, "to": 100000000}]
    add_vis(vid, title, dv, {"type": "metric", "aggs": [metric("1", "count", title)], "params": {
        "addTooltip": True, "addLegend": False, "type": "metric", "metric": {
            "percentageMode": False, "useRanges": True, "colorSchema": "Green to Red" if bad_above_zero else "Blues",
            "metricColorMode": "Background", "colorsRange": ranges, "labels": {"show": True}, "invertColors": False,
            "style": {"bgFill": "#000", "bgColor": False, "labelColor": False, "subText": "", "fontSize": 48}}}}, query)


def histogram(vid, title, dv, aggs, query=""):
    add_vis(vid, title, dv, {"type": "histogram", "aggs": aggs, "params": {
        "type": "histogram", "grid": {"categoryLines": False},
        "categoryAxes": [{"id": "CategoryAxis-1", "type": "category", "position": "bottom", "show": True, "style": {},
                          "scale": {"type": "linear"}, "labels": {"show": True, "filter": True, "truncate": 100}, "title": {}}],
        "valueAxes": [{"id": "ValueAxis-1", "name": "LeftAxis-1", "type": "value", "position": "left", "show": True, "style": {},
                       "scale": {"type": "linear", "mode": "normal"}, "labels": {"show": True, "rotate": 0, "filter": False, "truncate": 100},
                       "title": {"text": "Quantidade"}}],
        "seriesParams": [{"show": True, "type": "histogram", "mode": "stacked", "data": {"label": "Quantidade", "id": "1"},
                          "valueAxis": "ValueAxis-1", "drawLinesBetweenPoints": True, "lineWidth": 2, "showCircles": True}],
        "addTooltip": True, "addLegend": True, "legendPosition": "right", "times": [], "addTimeMarker": False,
        "labels": {"show": False}, "thresholdLine": {"show": False, "value": 10, "width": 1, "style": "full", "color": "#E7664C"}}}, query)


def markdown(vid, title, text):
    objects.append({"type": "visualization", "id": vid, "attributes": {
        "title": title, "visState": json.dumps({"title": title, "type": "markdown", "aggs": [], "params": {
            "fontSize": 12, "openLinksInNewTab": False, "markdown": text}}, ensure_ascii=False),
        "uiStateJSON": "{}", "description": "", "version": 1,
        "kibanaSavedObjectMeta": {"searchSourceJSON": json.dumps({"query": {"query": "", "language": "kuery"}, "filter": []})}}, "references": []})


def saved_search(sid, title, dv, query, columns, sort=None):
    objects.append({"type": "search", "id": sid, "attributes": {
        "title": title, "description": "", "hits": 0, "columns": columns, "sort": sort or [["@timestamp", "desc"]], "version": 1,
        "kibanaSavedObjectMeta": {"searchSourceJSON": src(query)}}, "references": ref(dv)})


def dashboard(did, title, description, panels, time_from):
    pj, refs = [], []
    for n, (pid, ptype, x, y, w, h) in enumerate(panels):
        pj.append({"version": "8.17.6", "type": ptype, "gridData": {"x": x, "y": y, "w": w, "h": h, "i": str(n)},
                   "panelIndex": str(n), "embeddableConfig": {}, "panelRefName": f"panel_{n}"})
        refs.append({"name": f"panel_{n}", "type": ptype, "id": pid})
    objects.append({"type": "dashboard", "id": did, "attributes": {
        "title": title, "description": description, "panelsJSON": json.dumps(pj),
        "optionsJSON": json.dumps({"useMargins": True, "syncColors": False, "syncCursor": True, "syncTooltips": False, "hidePanelTitles": False}),
        "timeRestore": True, "timeFrom": time_from, "timeTo": "now", "refreshInterval": {"pause": False, "value": 60000}, "version": 1,
        "kibanaSavedObjectMeta": {"searchSourceJSON": json.dumps({"query": {"query": "", "language": "kuery"}, "filter": []})}}, "references": refs})


# ================================================================= DASHBOARD 1: ERROS
ERR = 'log.level : ("Error" or "Fatal")'
REQ = 'message : "Request handled"'
SCAN_PATHS = ('metadata.LogDetails.RequestPath.Value : (*.env* or */.git* or */wp-json* or */wp-login* or */wordpress* or */wp/* '
              'or */xmlrpc* or */phpmyadmin* or */index.php or */.aws* or */.ssh* or */vendor/phpunit*)')

markdown("obs-err-help", "Como ler: erros", (
    "### Erros e saúde da plataforma\n\n"
    "- **Fonte**: logs estruturados dos serviços (índices `logs-*-production`). Volume baixo hoje; poucos erros já são relevantes.\n"
    "- **Erros** = nível `Error` ou `Fatal`. **5xx** = respostas de erro do servidor nos BFFs. **Falhas de consumer** = "
    "mensagens de fila que o `core_subscribers` não processou.\n"
    "- **Scanner com 2xx** deve ser sempre **0**: alguém pediu `.env`, `.git`, `wp-json` etc. e o servidor respondeu com sucesso.\n"
    "- Tráfego de scanners (`/.env`, `/wp-json`, `/.git/config`) é ruído esperado na internet aberta e aparece na tabela de rotas 404.\n"
    "- Histórico útil: 47 `Fatal` do `core_subscribers` (\"Client negotiate failed\", Azure SignalR) de 2026-08-18 a 2026-09-24 "
    "foram a causa do push em tempo real quebrado, corrigido em 2026-09-24."))
big_number("obs-err-total", "Erros (Error + Fatal)", "logs-all", ERR)
big_number("obs-err-5xx", "Respostas HTTP 5xx", "logs-http", f'{REQ} and metadata.LogDetails.StatusCode >= 500')
big_number("obs-err-consumers", "Falhas de consumer de fila", "logs-http", 'message : "Message handled" and metadata.LogDetails.Success : false')
big_number("obs-err-scanner-ok", "Scanner com resposta 2xx", "logs-http",
           f'{REQ} and {SCAN_PATHS} and metadata.LogDetails.StatusCode >= 200 and metadata.LogDetails.StatusCode < 300')

histogram("obs-err-timeline", "Erros por serviço ao longo do tempo", "logs-all",
          [metric("1", "count", "Erros"), date_hist("2"), terms("3", "service.name", "Serviço", "1", "desc", 10, "group")], ERR)
table("obs-err-types", "Erros por tipo de exceção", "logs-all",
      [metric("1", "count", "Ocorrências"), metric("4", "max", "Última vez", "@timestamp"),
       terms("2", "service.name", "Serviço", "1", "desc", 10), terms("3", "error.type", "Tipo", "1", "desc", 10)], ERR)
table("obs-err-templates", "Erros por mensagem (modelo)", "logs-all",
      [metric("1", "count", "Ocorrências"), metric("4", "max", "Última vez", "@timestamp"),
       terms("2", "service.name", "Serviço", "1", "desc", 10), terms("3", "labels.MessageTemplate", "Mensagem", "1", "desc", 10)], ERR)
table("obs-http-status", "Status HTTP por serviço", "logs-http",
      [metric("1", "count", "Requisições"), terms("2", "service.name", "Serviço", "1", "desc", 10),
       terms("3", "metadata.LogDetails.StatusCode", "Status", "_key", "asc", 15)], REQ)
table("obs-http-5xx", "Endpoints com erro 5xx", "logs-http",
      [metric("1", "count", "Ocorrências"), metric("4", "avg", "Tempo médio (ms)", "metadata.LogDetails.ElapsedMilliseconds"),
       terms("2", "endpoint", "Endpoint", "1", "desc", 10)], f'{REQ} and metadata.LogDetails.StatusCode >= 500')
table("obs-http-slow", "Endpoints lentos (>= 1 s)", "logs-http",
      [metric("1", "count", "Requisições lentas"), metric("4", "avg", "Tempo médio (ms)", "metadata.LogDetails.ElapsedMilliseconds"),
       metric("5", "max", "Pior (ms)", "metadata.LogDetails.ElapsedMilliseconds"),
       terms("2", "endpoint", "Endpoint", "1", "desc", 10)], f'{REQ} and metadata.LogDetails.ElapsedMilliseconds >= 1000')
table("obs-grpc-errors", "gRPC com falha, por método", "logs-grpc",
      [metric("1", "count", "Ocorrências"), terms("2", "service.name", "Serviço", "1", "desc", 10),
       terms("3", "metadata.LogDetails.Method", "Método", "1", "desc", 10), terms("4", "metadata.LogDetails.StatusCode", "Status gRPC", "1", "desc", 5)],
      f'{REQ} and not metadata.LogDetails.StatusCode : "OK"')
table("obs-404-paths", "Rotas inexistentes (404): scanners e cliques errados", "logs-http",
      [metric("1", "count", "Requisições"), terms("2", "service.name", "Serviço", "1", "desc", 5),
       terms("3", "metadata.LogDetails.RequestPath.Value", "Caminho", "1", "desc", 12)], f'{REQ} and metadata.LogDetails.StatusCode : 404')
saved_search("obs-err-recent", "Últimos erros", "logs-all", ERR,
             ["service.name", "log.level", "message", "error.type", "url.path"])

dashboard("observabilidade-erros", "Erros e saúde",
          "Erros, respostas 5xx, falhas de fila, endpoints lentos e tráfego suspeito, a partir dos logs dos serviços.", [
    ("obs-err-help", "visualization", 0, 0, 16, 10),
    ("obs-err-total", "visualization", 16, 0, 8, 10),
    ("obs-err-5xx", "visualization", 24, 0, 8, 10),
    ("obs-err-consumers", "visualization", 32, 0, 8, 10),
    ("obs-err-scanner-ok", "visualization", 40, 0, 8, 10),
    ("obs-err-timeline", "visualization", 0, 10, 28, 14),
    ("obs-err-types", "visualization", 28, 10, 20, 14),
    ("obs-err-templates", "visualization", 0, 24, 24, 14),
    ("obs-http-status", "visualization", 24, 24, 24, 14),
    ("obs-http-5xx", "visualization", 0, 38, 24, 14),
    ("obs-http-slow", "visualization", 24, 38, 24, 14),
    ("obs-grpc-errors", "visualization", 0, 52, 24, 14),
    ("obs-404-paths", "visualization", 24, 52, 24, 14),
    ("obs-err-recent", "search", 0, 66, 48, 16),
], "now-7d")

# ================================================================= DASHBOARD 2: USO / NEGÓCIO
OK = 'metadata.LogDetails.StatusCode >= 200 and metadata.LogDetails.StatusCode < 300'
P = 'metadata.LogDetails.RequestPath.Value'
M = 'metadata.LogDetails.RequestMethod'


def ev(service, method, path):
    parts = []
    if service:
        parts.append(f'service.name : "{service}"')
    parts += [f'{M} : "{method}"', f'{P} : {path}', OK]
    return " and ".join(parts)


TALENT, CONS, VOC = "re.colocar.me.talent", "consultant-bff", "vocacional-bff"
events = [
    ("Login: consultor", ev(CONS, "POST", "/auth/authenticate")),
    ("Login: talento", ev(TALENT, "POST", "/auth/authenticate")),
    ("Aceite dos termos", ev(None, "PUT", "*/accept-terms")),
    ("Currículo enviado: talento", ev(TALENT, "POST", "/profile/*/resume-upload")),
    ("Currículo enviado: consultor", ev(CONS, "POST", "/profile/consultant/*/resume-upload")),
    ("Teste vocacional iniciado", ev(VOC, "POST", "/vocacional/triagem")),
    ("Teste vocacional concluído", ev(VOC, "POST", "/vocacional/sessions/*/complete")),
    ("Resultado vinculado ao perfil", ev(TALENT, "POST", "/profile/*/vocational-result/link")),
    ("Talento conectou com consultor", ev(TALENT, "POST", "/consultants/*/connect")),
    ("Tratativa movida (kanban)", ev(CONS, "PUT", "/tratativas/*/stage")),
    ("Compra de Lemon Coins (checkout)", ev(None, "POST", "*/purchase/checkout")),
    ("Pagamento confirmado (Asaas)", ev(CONS, "POST", "/wallet/webhooks/asaas")),
    ("Serviço comprado com consultor", ev(TALENT, "POST", "/consultants/*/services/*/purchase")),
    ("Sessão agendada pelo talento", ev(TALENT, "POST", "/consultants/service-purchases/*/schedule")),
    ("Agenda do consultor alterada", ev(CONS, "PUT", "/schedule/*")),
    ("Preço de serviço alterado", ev(CONS, "PUT", "/services/*/price")),
]
ai_tools = [
    ("SWOT (talento)", ev(TALENT, "POST", "/profile/*/swot-analysis")),
    ("Sugestão de LinkedIn (talento)", ev(TALENT, "POST", "/profile/*/linkedin-suggestion")),
    ("Simulação de entrevista (talento)", ev(TALENT, "POST", "/profile/*/interview-simulation/*")),
    ("Carta de apresentação (talento)", ev(TALENT, "POST", "/profile/*/cover-letter/*")),
    ("Radar de lacunas (talento)", ev(TALENT, "POST", "/profile/*/skill-gap-analysis")),
    ("Resumo/bio com IA (consultor)", ev(CONS, "POST", "/resume/*/summary")),
]
funnel = [
    ("1. Triagem iniciada", ev(VOC, "POST", "/vocacional/triagem")),
    ("2. Respondeu perguntas", ev(VOC, "PUT", "/vocacional/sessions/*/answers")),
    ("3. Concluiu o teste", ev(VOC, "POST", "/vocacional/sessions/*/complete")),
]

markdown("obs-biz-help", "Como ler: uso da plataforma", (
    "### Uso da plataforma (a partir dos logs de request)\n\n"
    "- **Fonte**: requisições **bem-sucedidas (2xx)** dos BFFs. Cada linha é um evento de negócio reconhecido pelo método e caminho.\n"
    "- **Isto mede uso e atividade, não faturamento**: valores oficiais (saldos, transações, cadastros) estão no banco "
    "e no painel do backoffice. Recargas, retentativas e cliques repetidos contam mais de uma vez.\n"
    "- **Perfis ativos** = quantidade de identificadores de perfil distintos que aparecem nas rotas do próprio usuário "
    "(perfil, notificações, carteira, agenda): uma aproximação de usuários ativos, sem dado pessoal.\n"
    "- Só entram no painel eventos com endpoint conhecido; um recurso novo precisa ser acrescentado à lista de eventos."))
big_number("obs-biz-logins", "Logins", "logs-http",
           f'{M} : "POST" and {P} : "/auth/authenticate" and {OK}', bad_above_zero=False)
big_number("obs-biz-resumes", "Currículos enviados", "logs-http",
           f'{M} : "POST" and {P} : *resume-upload and {OK}', bad_above_zero=False)
big_number("obs-biz-vocacional", "Testes vocacionais concluídos", "logs-http",
           ev(VOC, "POST", "/vocacional/sessions/*/complete"), bad_above_zero=False)
big_number("obs-biz-checkout", "Compras de Lemon Coins iniciadas", "logs-http",
           ev(None, "POST", "*/purchase/checkout"), bad_above_zero=False)
table("obs-biz-events", "Eventos de negócio", "logs-http",
      [metric("1", "count", "Ocorrências"), filters_agg("2", events)], per_page=20)
histogram("obs-biz-timeline", "Eventos de negócio por dia", "logs-http",
          [metric("1", "count", "Ocorrências"), date_hist("2"), filters_agg("3", events, "Evento", "group")])
table("obs-biz-ai", "Uso das ferramentas de IA", "logs-http",
      [metric("1", "count", "Gerações"), filters_agg("2", ai_tools, "Ferramenta")])
table("obs-biz-funnel", "Funil do teste vocacional", "logs-http",
      [metric("1", "count", "Requisições"), metric("3", "cardinality", "Sessões distintas", P), filters_agg("2", funnel, "Etapa")])
histogram("obs-biz-active", "Perfis ativos por dia (aprox.)", "logs-http",
          [metric("1", "cardinality", "Perfis ativos", "owner_id"), date_hist("2"), terms("3", "service.name", "Serviço", "1", "desc", 6, "group")],
          f'{REQ} and {OK}')
table("obs-biz-audit", "Alterações administrativas (auditoria)", "audit-all",
      [metric("1", "count", "Alterações"), metric("5", "max", "Última", "@timestamp"),
       terms("2", "labels.Feature", "Funcionalidade", "1", "desc", 10), terms("3", "labels.Operation", "Operação", "1", "desc", 5),
       terms("4", "labels.ChangedBy", "Quem", "1", "desc", 5)])

dashboard("observabilidade-uso", "Uso da plataforma",
          "Atividade dos usuários (logins, currículos, teste vocacional, ferramentas de IA, carteira) a partir dos logs de request.", [
    ("obs-biz-help", "visualization", 0, 0, 16, 10),
    ("obs-biz-logins", "visualization", 16, 0, 8, 10),
    ("obs-biz-resumes", "visualization", 24, 0, 8, 10),
    ("obs-biz-vocacional", "visualization", 32, 0, 8, 10),
    ("obs-biz-checkout", "visualization", 40, 0, 8, 10),
    ("obs-biz-events", "visualization", 0, 10, 24, 18),
    ("obs-biz-timeline", "visualization", 24, 10, 24, 18),
    ("obs-biz-ai", "visualization", 0, 28, 24, 10),
    ("obs-biz-funnel", "visualization", 24, 28, 24, 10),
    ("obs-biz-active", "visualization", 0, 38, 48, 14),
    ("obs-biz-audit", "visualization", 0, 52, 48, 12),
], "now-30d")

with open(os.path.join(OUT, 'observability-dataviews.json'), 'w', encoding='utf-8') as f:
    json.dump(data_views, f, ensure_ascii=False, indent=2)
with open(os.path.join(OUT, 'observability-dashboards.json'), 'w', encoding='utf-8') as f:
    json.dump(objects, f, ensure_ascii=False, indent=2)
print('data views:', [d['data_view']['id'] for d in data_views])
print('objetos:', len(objects), 'dashboards:', [o['id'] for o in objects if o['type'] == 'dashboard'])
