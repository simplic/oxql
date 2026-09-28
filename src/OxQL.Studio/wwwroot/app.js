/*
 * OxQL console: a slim developer console for one service (DESIGN §7).
 * Monaco editor on one scratch query, Run (/query, /batch), Explain (/explain, markers from
 * `errors`, `valid: false` as an answer, the index advisory only on request), the health panel,
 * and a credential kept in sessionStorage with its decoded claims and an expiry warning.
 * The credential is never logged and never sent anywhere but this service's API.
 */
(function () {
    "use strict";

    // ── Config ───────────────────────────────────────────────────────────
    const cfg = JSON.parse(document.getElementById("oxql-config").textContent);
    const API = String(cfg.apiBasePath || "").replace(/\/$/, "");
    const SCHEMA = String(cfg.schemaBasePath || "").replace(/\/$/, "");
    const MONACO_BASE = String(cfg.monacoCdnBase || "").replace(/\/$/, "");
    const CONTRACT = "2";
    const EXPIRY_WARNING_MS = 5 * 60 * 1000;

    // One namespace per service (its API path), so every service keeps its own scratch query.
    const NS = "oxql.console." + API.replace(/[^a-z0-9]/gi, "_").toLowerCase();
    const KEY_SCRATCH = NS + ".scratch";
    const KEY_TOKEN = NS + ".bearer";
    const LEGACY_NS = "oxql.studio." + API.replace(/[^a-z0-9]/gi, "_").toLowerCase();

    const STARTER = {
        entityType: "",
        pipeline: [
            { match: {} },
            { sort: [{ id: "asc" }] },
            { page: { limit: 25 } }
        ]
    };

    // ── State ────────────────────────────────────────────────────────────
    let editor = null;
    let lastAnswer = null;
    let entityNames = [];
    let capabilities = null;   // null until /health answered

    const $ = (sel) => document.querySelector(sel);
    const tokenInput = $("#token-input");
    const statusEl = $("#status");
    const resultsEl = $("#results");

    // ── Storage (every access guarded: private windows may refuse it) ───
    function read(store, key) { try { return store.getItem(key); } catch { return null; } }
    function write(store, key, value) { try { value ? store.setItem(key, value) : store.removeItem(key); } catch { /* not stored */ } }

    function loadScratch() {
        const saved = read(localStorage, KEY_SCRATCH);
        if (saved) return saved;
        // The earlier tabbed studio kept its queries under another namespace: take its active tab once.
        try {
            const tabs = JSON.parse(read(localStorage, LEGACY_NS + ".tabs.v2") || "[]");
            const active = read(localStorage, LEGACY_NS + ".activeTab.v2");
            const tab = tabs.find(t => t.id === active) || tabs[0];
            if (tab && tab.content) return tab.content;
        } catch { /* nothing to take over */ }
        return JSON.stringify(STARTER, null, 2);
    }

    // ── Credential ───────────────────────────────────────────────────────
    function token() { return tokenInput.value.trim(); }

    function saveToken() {
        write(sessionStorage, KEY_TOKEN, token());
        renderClaims();
    }

    /** The JWT payload, or null for an opaque or malformed credential. */
    function decodeClaims(value) {
        const parts = value.split(".");
        if (parts.length !== 3) return null;
        try {
            const b64 = parts[1].replace(/-/g, "+").replace(/_/g, "/");
            const padded = b64 + "=".repeat((4 - b64.length % 4) % 4);
            const bytes = Uint8Array.from(atob(padded), c => c.charCodeAt(0));
            const claims = JSON.parse(new TextDecoder().decode(bytes));
            return claims && typeof claims === "object" ? claims : null;
        } catch { return null; }
    }

    function minutes(ms) {
        const m = Math.round(Math.abs(ms) / 60000);
        return m < 1 ? "less than a minute" : m < 120 ? `${m} min` : `${Math.round(m / 60)} h`;
    }

    /** The expiry line and its level: "ok", "warn" (soon) or "err" (expired / not yet valid). */
    function expiryOf(claims) {
        const now = Date.now();
        if (typeof claims.nbf === "number" && claims.nbf * 1000 > now)
            return { level: "err", text: `not valid for another ${minutes(claims.nbf * 1000 - now)}` };
        if (typeof claims.exp !== "number") return { level: "ok", text: "no expiry" };
        const left = claims.exp * 1000 - now;
        if (left <= 0) return { level: "err", text: `expired ${minutes(left)} ago` };
        if (left <= EXPIRY_WARNING_MS) return { level: "warn", text: `expires in ${minutes(left)}` };
        return { level: "ok", text: `valid until ${new Date(claims.exp * 1000).toLocaleTimeString()}` };
    }

    function renderClaims() {
        const badge = $("#claims-badge");
        const panel = $("#claims-panel");
        const value = token();
        if (!value) {
            badge.hidden = true;
            panel.hidden = true;
            return;
        }
        const claims = decodeClaims(value);
        badge.hidden = false;
        if (!claims) {
            badge.textContent = "opaque credential";
            badge.className = "badge";
            panel.innerHTML = `<div class="empty">The credential is not a JWT; it is sent as is.</div>`;
            return;
        }
        const expiry = expiryOf(claims);
        const who = claims.name || claims.email || claims.preferred_username || claims.sub || "unknown subject";
        badge.textContent = `${who} · ${expiry.text}`;
        badge.className = "badge " + expiry.level;
        const rows = Object.entries(claims).map(([k, v]) => {
            let shown = typeof v === "object" ? JSON.stringify(v) : String(v);
            if ((k === "exp" || k === "nbf" || k === "iat") && typeof v === "number") shown += ` (${new Date(v * 1000).toLocaleString()})`;
            return `<tr><th>${esc(k)}</th><td>${esc(shown)}</td></tr>`;
        });
        panel.innerHTML = `<div class="panel-title ${expiry.level}">${esc(expiry.text)}</div><table class="kv">${rows.join("")}</table>`;
    }

    function headers(withBody) {
        const h = { "X-OxQL-Contract": CONTRACT };
        if (withBody) h["Content-Type"] = "application/json";
        const value = token();
        if (value) h["Authorization"] = "Bearer " + value;
        return h;
    }

    // ── Editor ───────────────────────────────────────────────────────────
    function initMonaco() {
        return new Promise((resolve, reject) => {
            const loader = document.createElement("script");
            loader.src = MONACO_BASE + "/vs/loader.js";
            loader.onerror = () => reject(new Error("Monaco could not be loaded from " + MONACO_BASE));
            loader.onload = () => {
                window.require.config({ paths: { vs: MONACO_BASE + "/vs" } });
                window.require(["vs/editor/editor.main"], () => {
                    const model = monaco.editor.createModel(loadScratch(), "json", monaco.Uri.parse("inmemory://oxql/scratch.oxql.json"));
                    model.onDidChangeContent(() => {
                        write(localStorage, KEY_SCRATCH, model.getValue());
                        monaco.editor.setModelMarkers(model, "oxql", []);
                    });
                    editor = monaco.editor.create($("#editor"), {
                        model,
                        theme: "vs-dark",
                        automaticLayout: true,
                        fontSize: 13,
                        fontFamily: "'Cascadia Code','JetBrains Mono',Consolas,monospace",
                        minimap: { enabled: false },
                        scrollBeyondLastLine: false,
                        tabSize: 2,
                        quickSuggestions: { other: true, comments: false, strings: true }
                    });
                    editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.Enter, run);
                    editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyMod.Shift | monaco.KeyCode.KeyE, () => explainAvailable() && explain(false));
                    registerEntityCompletion();
                    resolve();
                });
            };
            document.head.appendChild(loader);
        });
    }

    /** Completes the value of "entityType" with the entity names the schema declares. */
    function registerEntityCompletion() {
        monaco.languages.registerCompletionItemProvider("json", {
            triggerCharacters: ["\""],
            provideCompletionItems(model, position) {
                if (!model.uri.path.endsWith(".oxql.json")) return { suggestions: [] };
                const before = model.getLineContent(position.lineNumber).slice(0, position.column - 1);
                const m = /"entityType"\s*:\s*"([^"]*)$/.exec(before);
                if (!m) return { suggestions: [] };
                const range = new monaco.Range(position.lineNumber, position.column - m[1].length, position.lineNumber, position.column);
                return {
                    suggestions: entityNames.map(name => ({
                        label: name, kind: monaco.languages.CompletionItemKind.Class, insertText: name, range
                    }))
                };
            }
        });
    }

    async function loadEntityNames() {
        if (!SCHEMA) return;
        try {
            const res = await fetch(SCHEMA, { headers: headers(false) });
            if (!res.ok) return;
            const doc = await res.json();
            entityNames = Object.entries(doc?.types || {}).filter(([, t]) => t && t.entity).map(([id]) => id).sort();
        } catch { /* no schema on this host: no completion */ }
    }

    // ── Locating errors in the document ──────────────────────────────────
    /**
     * Walks the JSON text and records every value's range by JSON pointer ("/pipeline/3"), and
     * every property name's range as "<pointer>#key". Returns null for text that is not JSON.
     */
    function locate(text) {
        const ranges = {};
        let i = 0;
        const ws = () => { while (i < text.length && /\s/.test(text[i])) i++; };
        const str = () => { const s = i++; while (i < text.length && text[i] !== "\"") i += text[i] === "\\" ? 2 : 1; i++; return [s, i]; };
        function value(ptr) {
            ws();
            const start = i;
            if (text[i] === "{") {
                i++; ws();
                while (text[i] !== "}") {
                    if (text[i] !== "\"") throw new Error("key");
                    const [ks, ke] = str();
                    const key = JSON.parse(text.slice(ks, ke)).replace(/~/g, "~0").replace(/\//g, "~1");
                    ranges[ptr + "/" + key + "#key"] = [ks, ke];
                    ws(); if (text[i++] !== ":") throw new Error(":");
                    value(ptr + "/" + key); ws();
                    if (text[i] === ",") { i++; ws(); } else if (text[i] !== "}") throw new Error(",");
                }
                i++;
            } else if (text[i] === "[") {
                i++; ws();
                for (let n = 0; text[i] !== "]"; n++) {
                    value(ptr + "/" + n); ws();
                    if (text[i] === ",") { i++; ws(); } else if (text[i] !== "]") throw new Error(",");
                }
                i++;
            } else if (text[i] === "\"") {
                str();
            } else {
                while (i < text.length && /[^\s,\]}]/.test(text[i])) i++;
                if (i === start) throw new Error("value");
            }
            ranges[ptr] = [start, i];
        }
        try { value(""); return ranges; } catch { return null; }
    }

    /** The text range an error or note points at: its stage, narrowed to its path when the path is written there. */
    function rangeOf(ranges, text, item) {
        if (!ranges) return [0, 0];
        const stage = typeof item.stage === "number" ? ranges["/pipeline/" + item.stage] : null;
        const within = stage || ranges[""] || [0, text.length];
        if (item.path) {
            const segments = String(item.path).split(".");
            for (const needle of [item.path, segments[segments.length - 1]]) {
                const at = text.indexOf("\"" + needle + "\"", within[0]);
                if (at >= 0 && at < within[1]) return [at, at + needle.length + 2];
            }
        }
        if (stage) return stage;
        return ranges["/entityType"] || [0, 0];
    }

    function setMarkers(errors, notes) {
        if (!editor) return;
        const model = editor.getModel();
        const text = model.getValue();
        const ranges = locate(text);
        const marker = (item, severity) => {
            const [s, e] = rangeOf(ranges, text, item);
            const a = model.getPositionAt(s), b = model.getPositionAt(Math.max(e, s + 1));
            return {
                severity, message: `${item.code}: ${item.message || ""}`, source: "oxql",
                startLineNumber: a.lineNumber, startColumn: a.column, endLineNumber: b.lineNumber, endColumn: b.column
            };
        };
        monaco.editor.setModelMarkers(model, "oxql", [
            ...(errors || []).map(x => marker(x, monaco.MarkerSeverity.Error)),
            ...(notes || []).map(x => marker(x, monaco.MarkerSeverity.Info))
        ]);
    }

    function reveal(item) {
        if (!editor) return;
        const model = editor.getModel();
        const text = model.getValue();
        const [s] = rangeOf(locate(text), text, item);
        const pos = model.getPositionAt(s);
        editor.revealPositionInCenter(pos);
        editor.setPosition(pos);
        editor.focus();
    }

    // ── Requests ─────────────────────────────────────────────────────────
    function documentOrNull() {
        try {
            return JSON.parse(editor.getValue());
        } catch (e) {
            setStatus("Invalid JSON: " + e.message, "err");
            return null;
        }
    }

    async function post(path, body) {
        const started = performance.now();
        const res = await fetch(API + path, { method: "POST", headers: headers(true), body: JSON.stringify(body) });
        const elapsed = Math.round(performance.now() - started);
        const text = await res.text();
        let json;
        try { json = JSON.parse(text); } catch { json = text; }
        return { res, json, elapsed };
    }

    function refusalText(json) {
        return json && json.type ? `${json.type}` + (Array.isArray(json.errors) ? ": " + json.errors.map(e => e.code).join(", ") : "") : "";
    }

    async function run() {
        const body = documentOrNull();
        if (!body) return;
        const batch = Array.isArray(body.queries);
        setStatus(batch ? "Running batch…" : "Running…", "");
        try {
            const { res, json, elapsed } = await post(batch ? "/batch" : "/query", body);
            let summary = "";
            if (res.ok && Array.isArray(json?.items)) {
                const info = json.pageInfo || {};
                summary = `${json.items.length} item(s)`;
                if (info.totalCount != null) summary += ` · total ${info.totalCount}${info.totalCountCapped ? "+" : ""}`;
                if (info.hasNextPage) summary += " · more";
                if (Array.isArray(json.diagnostics) && json.diagnostics.length) summary += ` · ${json.diagnostics.length} diagnostic(s)`;
            } else if (res.ok && Array.isArray(json?.results)) {
                const refused = json.results.filter(r => r && r.type && !Array.isArray(r.items)).length;
                summary = `batch of ${json.results.length}` + (refused ? ` · ${refused} refused` : "");
            } else {
                summary = refusalText(json);
            }
            setStatus(`${res.status} · ${elapsed} ms · ${summary}`, res.ok ? "ok" : "err");
            setMarkers(!res.ok && Array.isArray(json?.errors) ? json.errors : [], []);
            renderRaw(json);
        } catch (e) {
            setStatus("Request failed: " + e.message, "err");
        }
    }

    async function explain(withIndexes) {
        const body = documentOrNull();
        if (!body) return;
        if (Array.isArray(body.queries)) { setStatus("Explain takes one query, not a batch.", "err"); return; }
        const envelope = { query: body };
        if (withIndexes) envelope.include = ["indexes"];
        setStatus(withIndexes ? "Explaining with the index advisory…" : "Explaining…", "");
        try {
            const { res, json, elapsed } = await post("/explain", envelope);
            if (res.ok && json && typeof json.valid === "boolean") {
                const errors = json.errors || [], notes = json.notes || [], steps = json.steps || [];
                setStatus(`Explain · ${elapsed} ms · ` + (json.valid ? "valid" : `invalid · ${errors.length} error(s)`) +
                    ` · ${steps.length} step(s) · ${notes.length} note(s)`, json.valid ? "ok" : "err");
                setMarkers(errors, notes);
                renderExplain(json);
            } else if (res.status === 404) {
                setStatus("404 · explain is switched off on this host (OxQL:Explain:Enabled)", "err");
                renderRaw(json);
            } else {
                setStatus(`${res.status} · ${elapsed} ms · ${refusalText(json)}`, "err");
                setMarkers(Array.isArray(json?.errors) ? json.errors : [], []);
                renderRaw(json);
            }
        } catch (e) {
            setStatus("Request failed: " + e.message, "err");
        }
    }

    // ── Rendering ────────────────────────────────────────────────────────
    function esc(s) {
        return String(s ?? "").replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
    }

    /** JSON as HTML with token classes; only &, < and > need escaping inside a <pre>. */
    function highlight(value) {
        const json = (typeof value === "string" ? value : JSON.stringify(value, null, 2) ?? "")
            .replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
        return json.replace(/("(\\u[a-fA-F0-9]{4}|\\[^u]|[^\\"])*"(\s*:)?|\b(true|false)\b|\bnull\b|-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)/g, (m) => {
            const cls = m.startsWith("\"") ? (/:$/.test(m) ? "tok-key" : "tok-str") : /true|false/.test(m) ? "tok-bool" : m === "null" ? "tok-null" : "tok-num";
            return `<span class="${cls}">${m}</span>`;
        });
    }

    function setStatus(text, cls) {
        statusEl.textContent = text;
        statusEl.className = "status" + (cls ? " " + cls : "");
    }

    function renderRaw(json) {
        lastAnswer = json;
        resultsEl.innerHTML = `<pre class="raw">${highlight(json)}</pre>`;
    }

    function section(title, inner, open) {
        return `<details class="section"${open ? " open" : ""}><summary>${esc(title)}</summary>${inner}</details>`;
    }

    function table(head, rows) {
        return `<table class="grid"><thead><tr>${head.map(h => `<th>${esc(h)}</th>`).join("")}</tr></thead><tbody>${rows.join("")}</tbody></table>`;
    }

    function renderExplain(answer) {
        lastAnswer = answer;
        const errors = answer.errors || [], notes = answer.notes || [], steps = answer.steps || [];
        const parts = [];
        parts.push(`<div class="verdict ${answer.valid ? "ok" : "err"}">${answer.valid ? "Valid" : "Invalid"} · contract ${esc(answer.contract)}` +
            (answer.engine ? ` · engine ${esc(answer.engine.version)}` : "") + "</div>");

        if (errors.length) {
            parts.push(section(`Errors · ${errors.length}`, table(["code", "stage", "path", "message"], errors.map((e, i) =>
                `<tr class="jump" data-kind="error" data-i="${i}"><td>${esc(e.code)}</td><td>${esc(e.stage ?? "")}</td><td>${esc(e.path ?? "")}</td><td>${esc(e.message)}</td></tr>`)), true));
        }
        if (steps.length) {
            parts.push(section(`Steps · ${steps.length}`, table(["#", "kind", "status", "executor", "phase", "owner", "creates"], steps.map(s =>
                `<tr class="st-${esc(s.status)}"><td>${esc(s.index)}</td><td>${esc(s.kind ?? "")}</td><td>${esc(s.status)}</td><td>${esc(s.executor ?? "")}</td>` +
                `<td>${esc(s.phase ?? "")}</td><td>${esc(s.owner?.service ?? "")}</td><td>${esc((s.creates || []).map(c => `${c.alias}:${c.node}`).join(", "))}</td></tr>`)), true));
        }
        if (notes.length) {
            parts.push(section(`Notes · ${notes.length}`, table(["code", "stage", "path", "message"], notes.map((n, i) =>
                `<tr class="jump" data-kind="note" data-i="${i}"><td>${esc(n.code)}</td><td>${esc(n.stage ?? "")}</td><td>${esc(n.path ?? "")}</td><td>${esc(n.message)}</td></tr>`)), true));
        }
        const columns = answer.result?.columns || [];
        if (columns.length) {
            parts.push(section(`Result · ${answer.result.paging} · ${columns.length} column(s)`, table(["path", "kind", "nullable", "root", "stage"], columns.map(c =>
                `<tr><td>${esc(c.path)}</td><td>${esc(c.kind)}</td><td>${c.nullable ? "yes" : "no"}</td><td>${esc(c.root)}</td><td>${esc(c.stage ?? "")}</td></tr>`)), false));
        }
        const owners = steps.filter(s => s.owner);
        if (owners.length) {
            parts.push(section(`Owner queries · ${owners.length}`, owners.map(s =>
                `<div class="sub">step ${esc(s.index)} → ${esc(s.owner.service)}${s.owner.route ? ` (${esc(s.owner.route.apiName)})` : ""}</div><pre class="raw">${highlight(s.owner.query)}</pre>`).join(""), false));
        }
        if (Array.isArray(answer.advisory)) {
            parts.push(section(`Index advisory · ${answer.advisory.length}`, table(["field", "used", "index", "note"], answer.advisory.map(a =>
                `<tr><td>${esc(a.field)}</td><td class="used-${a.used === true ? "yes" : a.used === false ? "no" : "unknown"}">${a.used === true ? "yes" : a.used === false ? "no" : "?"}</td><td>${esc(a.index ?? "")}</td><td>${esc(a.note ?? "")}</td></tr>`)), true));
        }
        if (Array.isArray(answer.diagnostics) && answer.diagnostics.length) parts.push(section(`Diagnostics · ${answer.diagnostics.length}`, `<pre class="raw">${highlight(answer.diagnostics)}</pre>`, true));
        if (Array.isArray(answer.stages)) parts.push(section(`Compiled page stages · ${answer.stages.length}`, `<pre class="raw">${highlight(answer.stages)}</pre>`, false));
        if (Array.isArray(answer.count)) parts.push(section(`Compiled count stages · ${answer.count.length}`, `<pre class="raw">${highlight(answer.count)}</pre>`, false));
        if (answer.bound !== undefined) parts.push(section("Bound form", `<pre class="raw">${highlight(answer.bound)}</pre>`, false));
        parts.push(section("Raw answer", `<pre class="raw">${highlight(answer)}</pre>`, false));

        resultsEl.innerHTML = parts.join("");
        resultsEl.querySelectorAll("tr.jump").forEach(tr => tr.addEventListener("click", () => {
            const list = tr.dataset.kind === "error" ? errors : notes;
            reveal(list[Number(tr.dataset.i)]);
        }));
    }

    // ── Health ───────────────────────────────────────────────────────────
    function explainAvailable() {
        return !!cfg.enableExplain && (capabilities === null || capabilities.includes("explain"));
    }

    function reflectExplain() {
        const on = explainAvailable();
        $("#explain-btn").hidden = !on;
        $("#indexes-btn").hidden = !on;
        $("#hint").textContent = "One scratch query per service, saved in this browser · Ctrl+Enter to run" + (on ? " · Ctrl+Shift+E to explain" : "");
    }

    async function loadHealth() {
        const badge = $("#engine-badge");
        const panel = $("#health-panel");
        try {
            const res = await fetch(API + "/health", { headers: headers(false) });
            const json = await res.json();
            capabilities = Array.isArray(json?.capabilities) ? json.capabilities : [];
            badge.textContent = `engine ${json?.engine?.version || "?"} · contract ${json?.engine?.contract ?? "?"} · ${json?.status || res.status}`;
            badge.className = "badge " + (res.ok && json?.status !== "degraded" ? "ok" : "warn");
            const remote = Array.isArray(json?.remote) ? json.remote : [];
            panel.innerHTML =
                `<div class="panel-title">Capabilities</div><div class="chips">${capabilities.map(c => `<span class="chip">${esc(c)}</span>`).join("") || "none"}</div>` +
                (remote.length ? `<div class="panel-title">Remote services</div>` + table(Object.keys(remote[0]), remote.map(r => `<tr>${Object.values(r).map(v => `<td>${esc(typeof v === "object" ? JSON.stringify(v) : v)}</td>`).join("")}</tr>`)) : "") +
                `<div class="panel-title">Limits</div><table class="kv">${Object.entries(json?.limits || {}).map(([k, v]) => `<tr><th>${esc(k)}</th><td>${esc(v)}</td></tr>`).join("")}</table>`;
        } catch {
            capabilities = null;
            badge.textContent = "engine unreachable";
            badge.className = "badge err";
            panel.innerHTML = `<div class="empty">GET ${esc(API)}/health did not answer.</div>`;
        }
        reflectExplain();
    }

    function toggle(panel) {
        const el = $(panel);
        el.hidden = !el.hidden;
    }

    // ── Wiring ───────────────────────────────────────────────────────────
    function bind() {
        $("#run-btn").addEventListener("click", run);
        $("#explain-btn").addEventListener("click", () => explain(false));
        $("#indexes-btn").addEventListener("click", () => explain(true));
        $("#format-btn").addEventListener("click", () => editor?.getAction("editor.action.formatDocument")?.run());
        $("#reset-btn").addEventListener("click", () => editor?.setValue(JSON.stringify(STARTER, null, 2)));
        $("#copy-result").addEventListener("click", () => {
            if (lastAnswer !== null) navigator.clipboard?.writeText(typeof lastAnswer === "string" ? lastAnswer : JSON.stringify(lastAnswer, null, 2));
        });
        $("#engine-badge").addEventListener("click", () => toggle("#health-panel"));
        $("#claims-badge").addEventListener("click", () => toggle("#claims-panel"));
        tokenInput.addEventListener("input", saveToken);
        $("#token-toggle").addEventListener("click", () => { tokenInput.type = tokenInput.type === "password" ? "text" : "password"; });
        $("#token-clear").addEventListener("click", () => { tokenInput.value = ""; saveToken(); });

        const link = $("#studio-link");
        if (cfg.studioAppUrl) {
            try {
                const url = new URL(cfg.studioAppUrl, location.href);
                if (url.protocol === "http:" || url.protocol === "https:") { link.href = url.href; link.hidden = false; }
            } catch { /* not a URL: no link */ }
        }
    }

    async function boot() {
        // The earlier studio kept the credential in localStorage; it lives in sessionStorage now.
        write(localStorage, LEGACY_NS + ".bearer.v1", "");
        tokenInput.value = read(sessionStorage, KEY_TOKEN) || "";
        renderClaims();
        setInterval(renderClaims, 30000);
        bind();
        reflectExplain();
        loadHealth();
        loadEntityNames();
        try {
            await initMonaco();
            setStatus("Ready", "");
        } catch (e) {
            setStatus(e.message, "err");
        }
    }

    boot();
})();
