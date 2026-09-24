(function () {
    "use strict";

    // ── Config ───────────────────────────────────────────────────────────
    const cfg = JSON.parse(document.getElementById("oxql-config").textContent);
    const API = cfg.apiBasePath.replace(/\/$/, "");
    const SCHEMA = (cfg.schemaBasePath || "").replace(/\/$/, "");
    const MONACO_BASE = cfg.monacoCdnBase.replace(/\/$/, "");
    const CONTRACT_HEADER = "X-OxQL-Contract";

    // Namespace localStorage keys per API endpoint so that multiple microservices
    // (e.g. logistics-api, erp-api) each maintain their own isolated cache.
    const LS_NS = "oxql.studio." + API.replace(/[^a-z0-9]/gi, "_").toLowerCase();
    const LS_TABS = LS_NS + ".tabs.v2";
    const LS_ACTIVE = LS_NS + ".activeTab.v2";
    const LS_TOKEN = LS_NS + ".bearer.v1";
    const LS_CONTRACT = LS_NS + ".contract.v2";

    const DEFAULT_QUERY = {
        entityType: "vehicle.vehicle",
        pipeline: [
            { match: { matchCode: { neq: null } } },
            { sort: [{ id: "asc" }] },
            { page: { limit: 25 } }
        ]
    };

    // ── State ────────────────────────────────────────────────────────────
    let tabs = [];          // [{ id, name, entityType, content }]
    let activeId = null;
    let editor = null;
    let monacoModelsByTab = {};   // id -> monaco model
    let typesCache = [];          // entities from /schema (+ addon definitions), for the explorer and the wizard

    // ── DOM ──────────────────────────────────────────────────────────────
    const $ = (sel) => document.querySelector(sel);
    const tabbar = $("#tabbar");
    const tabAdd = $("#tab-add");
    const tokenInput = $("#token-input");
    const tokenDot = $("#token-dot");
    const entityInput = $("#entity-input");
    const contractSelect = $("#contract-select");
    const statusEl = $("#status");
    const resultsEl = $("#results");

    // ── LocalStorage helpers ─────────────────────────────────────────────
    function loadTabs() {
        try {
            const raw = localStorage.getItem(LS_TABS);
            if (raw) tabs = JSON.parse(raw);
        } catch { tabs = []; }

        if (!Array.isArray(tabs) || tabs.length === 0) {
            tabs = [makeTab("Query 1", DEFAULT_QUERY)];
        }
        activeId = localStorage.getItem(LS_ACTIVE) || tabs[0].id;
        if (!tabs.some(t => t.id === activeId)) activeId = tabs[0].id;
    }

    function saveTabs() {
        // Persist current editor content into the active tab first.
        syncActiveFromEditor();
        localStorage.setItem(LS_TABS, JSON.stringify(tabs));
        localStorage.setItem(LS_ACTIVE, activeId);
    }

    function makeTab(name, queryObj) {
        return {
            id: "t_" + Math.random().toString(36).slice(2, 9),
            name: name,
            entityType: queryObj?.entityType || "",
            content: JSON.stringify(queryObj || {}, null, 2)
        };
    }

    // ── Token, contract ──────────────────────────────────────────────────
    function loadToken() {
        const t = localStorage.getItem(LS_TOKEN) || "";
        tokenInput.value = t;
        reflectTokenDot();
    }
    function saveToken() {
        localStorage.setItem(LS_TOKEN, tokenInput.value.trim());
        reflectTokenDot();
    }
    function reflectTokenDot() {
        tokenDot.classList.toggle("set", !!tokenInput.value.trim());
    }

    function loadContract() {
        const c = localStorage.getItem(LS_CONTRACT);
        contractSelect.value = c === "1" ? "1" : "2";
    }
    function saveContract() {
        localStorage.setItem(LS_CONTRACT, contractSelect.value);
    }

    /** The headers every request carries: the contract, the bearer token, and the body type when there is one. */
    function apiHeaders(withBody) {
        const headers = { [CONTRACT_HEADER]: contractSelect.value || "2" };
        if (withBody) headers["Content-Type"] = "application/json";
        const token = tokenInput.value.trim();
        if (token) headers["Authorization"] = "Bearer " + token;
        return headers;
    }

    // ── Tab rendering ────────────────────────────────────────────────────
    function renderTabs() {
        // Remove existing tab elements (keep the add button)
        [...tabbar.querySelectorAll(".tab")].forEach(el => el.remove());

        for (const t of tabs) {
            const el = document.createElement("div");
            el.className = "tab" + (t.id === activeId ? " active" : "");
            el.dataset.id = t.id;

            const title = document.createElement("span");
            title.className = "tab-title";
            title.textContent = t.name;
            title.title = "Double-click to rename";
            el.appendChild(title);

            const close = document.createElement("span");
            close.className = "close";
            close.textContent = "✕";
            close.title = "Close tab";
            close.addEventListener("click", (e) => { e.stopPropagation(); closeTab(t.id); });
            el.appendChild(close);

            el.addEventListener("click", () => activateTab(t.id));
            title.addEventListener("dblclick", (e) => { e.stopPropagation(); beginRename(title, t); });

            tabbar.insertBefore(el, tabAdd);
        }
    }

    function beginRename(titleEl, tab) {
        titleEl.setAttribute("contenteditable", "true");
        titleEl.focus();
        document.execCommand?.("selectAll", false, null);

        const commit = () => {
            titleEl.removeAttribute("contenteditable");
            const name = titleEl.textContent.trim() || tab.name;
            tab.name = name;
            titleEl.textContent = name;
            saveTabs();
        };
        titleEl.addEventListener("blur", commit, { once: true });
        titleEl.addEventListener("keydown", (e) => {
            if (e.key === "Enter") { e.preventDefault(); titleEl.blur(); }
        });
    }

    function activateTab(id) {
        if (id === activeId) return;
        syncActiveFromEditor();
        activeId = id;
        localStorage.setItem(LS_ACTIVE, activeId);
        swapEditorModel();
        const tab = tabs.find(t => t.id === id);
        entityInput.value = tab?.entityType || "";
        renderTabs();
    }

    function addTab() {
        const n = tabs.length + 1;
        const tab = makeTab("Query " + n, { entityType: "", pipeline: [] });
        tabs.push(tab);
        activeId = tab.id;
        saveTabs();
        swapEditorModel();
        entityInput.value = "";
        renderTabs();
    }

    function closeTab(id) {
        const idx = tabs.findIndex(t => t.id === id);
        if (idx === -1) return;

        // Dispose the monaco model for the closed tab
        if (monacoModelsByTab[id]) {
            monacoModelsByTab[id].dispose();
            delete monacoModelsByTab[id];
        }

        tabs.splice(idx, 1);
        if (tabs.length === 0) tabs = [makeTab("Query 1", DEFAULT_QUERY)];

        if (activeId === id) {
            activeId = tabs[Math.max(0, idx - 1)].id;
            swapEditorModel();
            const tab = tabs.find(t => t.id === activeId);
            entityInput.value = tab?.entityType || "";
        }
        saveTabs();
        renderTabs();
    }

    // ── Editor / Monaco ──────────────────────────────────────────────────
    function getModelForTab(tab) {
        if (monacoModelsByTab[tab.id]) return monacoModelsByTab[tab.id];
        // Give every model an .oxql.json URI so the OxQL JSON schema is applied.
        const uri = monaco.Uri.parse(`inmemory://oxql/${tab.id}.oxql.json`);
        const model = monaco.editor.createModel(tab.content, "json", uri);
        model.onDidChangeContent(() => {
            tab.content = model.getValue();
        });
        monacoModelsByTab[tab.id] = model;
        return model;
    }

    function swapEditorModel() {
        const tab = tabs.find(t => t.id === activeId);
        if (!tab || !editor) return;
        editor.setModel(getModelForTab(tab));
    }

    function syncActiveFromEditor() {
        if (!editor) return;
        const tab = tabs.find(t => t.id === activeId);
        if (tab) tab.content = editor.getValue();
    }

    function initMonaco() {
        return new Promise((resolve) => {
            const loaderScript = document.createElement("script");
            loaderScript.src = MONACO_BASE + "/vs/loader.js";
            loaderScript.onload = () => {
                window.require.config({ paths: { vs: MONACO_BASE + "/vs" } });
                window.require(["vs/editor/editor.main"], () => {
                    monaco.editor.defineTheme("oxql-dark", {
                        base: "vs-dark",
                        inherit: true,
                        rules: [],
                        colors: {
                            "editor.background": "#0d1117",
                            "editorGutter.background": "#0d1117",
                            "editor.lineHighlightBackground": "#161b22",
                            "editorLineNumber.foreground": "#484f58",
                            "editorLineNumber.activeForeground": "#adbac7"
                        }
                    });

                    // Register the OxQL JSON schema + completion hints
                    registerOxQLLanguageFeatures();

                    const firstTab = tabs.find(t => t.id === activeId) || tabs[0];
                    editor = monaco.editor.create(document.getElementById("editor"), {
                        model: getModelForTab(firstTab),
                        theme: "oxql-dark",
                        language: "json",
                        automaticLayout: true,
                        fontSize: 13,
                        fontFamily: "'Cascadia Code','JetBrains Mono',Consolas,monospace",
                        minimap: { enabled: false },
                        scrollBeyondLastLine: false,
                        tabSize: 2,
                        renderWhitespace: "none",
                        bracketPairColorization: { enabled: true },
                        // JSON keeps property names & values inside strings, and Monaco
                        // disables quick suggestions in strings by default — enable them
                        // so completions appear while typing.
                        quickSuggestions: { other: true, comments: false, strings: true },
                        suggestOnTriggerCharacters: true,
                        suggest: { showWords: false, snippetsPreventQuickSuggestions: false }
                    });

                    // Ctrl+Enter runs the query
                    editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.Enter, runQuery);

                    // Ctrl+Shift+E explains the query (only when enabled)
                    if (cfg.enableExplain) {
                        editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyMod.Shift | monaco.KeyCode.KeyE, explainQuery);
                    }

                    // F1 opens the query help / cheat sheet
                    editor.addCommand(monaco.KeyCode.F1, openHelp);

                    resolve();
                });
            };
            document.head.appendChild(loaderScript);
        });
    }

    // ── OxQL language features (schema + completions + hover) ─────────────
    let oxqlFeaturesRegistered = false;

    function registerOxQLLanguageFeatures() {
        if (oxqlFeaturesRegistered || !window.OxQLLang) return;
        oxqlFeaturesRegistered = true;

        // JSON schema → IntelliSense, hover hints and inline validation.
        monaco.languages.json.jsonDefaults.setDiagnosticsOptions({
            validate: true,
            allowComments: false,
            enableSchemaRequest: false,
            schemas: [
                {
                    uri: "https://oxql.local/schema/query.json",
                    fileMatch: ["*.oxql.json", "*"],
                    schema: window.OxQLLang.schema
                }
            ]
        });

        // Map our snippet "kind" to a Monaco completion-item kind + sort weight + label.
        const kindMap = {
            stage:    { icon: () => monaco.languages.CompletionItemKind.Class,         sort: "1", tag: "stage" },
            logical:  { icon: () => monaco.languages.CompletionItemKind.Keyword,       sort: "2", tag: "logical" },
            operator: { icon: () => monaco.languages.CompletionItemKind.Operator,      sort: "3", tag: "operator" },
            operand:  { icon: () => monaco.languages.CompletionItemKind.TypeParameter, sort: "4", tag: "operand" },
            value:    { icon: () => monaco.languages.CompletionItemKind.Variable,      sort: "5", tag: "variable" },
            function: { icon: () => monaco.languages.CompletionItemKind.Function,      sort: "6", tag: "aggregation" }
        };

        // Show the snippet body (with placeholders stripped) as part of the docs.
        function snippetPreview(text) {
            return text.replace(/\$\{\d+:?([^}]*)\}/g, "$1");
        }

        // Snippet completions layered on top of the schema-driven suggestions.
        monaco.languages.registerCompletionItemProvider("json", {
            triggerCharacters: ["\"", "$", " ", "{", "[", ":"],
            provideCompletionItems(model, position) {
                // Only contribute inside our OxQL models.
                if (!model.uri.path.endsWith(".oxql.json")) return { suggestions: [] };

                const word = model.getWordUntilPosition(position);
                const range = new monaco.Range(
                    position.lineNumber, word.startColumn,
                    position.lineNumber, word.endColumn
                );

                const suggestions = window.OxQLLang.allSnippets().map(s => {
                    const meta = kindMap[s.kind] || kindMap.value;
                    const docMarkdown =
                        (s.documentation ? s.documentation + "\n\n" : "") +
                        "```json\n" + snippetPreview(s.insertText) + "\n```";

                    return {
                        // Plain-string label so the name always renders in the suggest widget.
                        label: s.label,
                        kind: meta.icon(),
                        // detail shows dimmed next to the name (category + summary).
                        detail: `${meta.tag} · ${s.detail || ""}`.trim(),
                        // documentation fills the side panel with docs + a snippet preview.
                        documentation: { value: docMarkdown },
                        insertText: s.insertText,
                        insertTextRules: monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet,
                        filterText: s.label,
                        sortText: meta.sort + s.label,
                        range
                    };
                });

                return { suggestions };
            }
        });
    }

    // ── Help / cheat-sheet drawer ────────────────────────────────────────
    function buildHelp() {
        const body = $("#help-body");
        if (!body || !window.OxQLLang) return;
        body.innerHTML = "";

        for (const section of window.OxQLLang.helpSections) {
            const group = document.createElement("div");
            group.className = "help-section";

            const title = document.createElement("div");
            title.className = "help-section-title";

            const caret = document.createElement("span");
            caret.className = "help-caret open";
            caret.textContent = "▸";
            title.appendChild(caret);

            const label = document.createElement("span");
            label.textContent = section.title;
            title.appendChild(label);

            group.appendChild(title);

            const items = document.createElement("div");
            items.className = "help-section-items";

            // Fold / unfold this section.
            title.addEventListener("click", () => {
                const collapsed = group.classList.toggle("collapsed");
                caret.classList.toggle("open", !collapsed);
            });

            for (const item of section.items) {
                const el = document.createElement("div");
                el.className = "help-item";
                el.dataset.search = (item.name + " " + (item.desc || "")).toLowerCase();

                const name = document.createElement("div");
                name.className = "hi-name";
                name.textContent = item.name;
                el.appendChild(name);

                if (item.desc) {
                    const desc = document.createElement("div");
                    desc.className = "hi-desc";
                    desc.textContent = item.desc;
                    el.appendChild(desc);
                }

                const payload = item.insert || item.snippet;
                if (payload) {
                    const pre = document.createElement("code");
                    pre.className = "hi-snippet";
                    // Show snippets without the ${n:...} placeholder markers.
                    pre.textContent = payload.replace(/\$\{\d+:?([^}]*)\}/g, "$1");
                    el.appendChild(pre);
                }

                el.addEventListener("click", () => {
                    if (item.insert) {
                        insertSnippetAtCursor(item.insert);
                    } else if (item.snippet) {
                        insertSnippetAtCursor(item.snippet);
                    }
                });

                items.appendChild(el);
            }

            group.appendChild(items);
            body.appendChild(group);
        }
    }

    function filterHelp(term) {
        term = (term || "").trim().toLowerCase();
        const groups = document.querySelectorAll("#help-body .help-section");
        groups.forEach(group => {
            const items = group.querySelectorAll(".help-item");
            let anyVisible = false;
            items.forEach(el => {
                const match = !term || el.dataset.search.includes(term);
                el.style.display = match ? "" : "none";
                if (match) anyVisible = true;
            });
            // Hide whole section when nothing matches; auto-expand when filtering.
            group.style.display = anyVisible ? "" : "none";
            if (term) {
                group.classList.remove("collapsed");
                group.querySelector(".help-caret")?.classList.add("open");
            }
        });
    }

    function openHelp() {
        const drawer = $("#help-drawer");
        const overlay = $("#help-overlay");
        if (!drawer.dataset.built) {
            buildHelp();
            drawer.dataset.built = "1";
        }
        overlay.hidden = false;
        drawer.hidden = false;
        $("#help-search-input")?.focus();
    }

    function closeHelp() {
        $("#help-drawer").hidden = true;
        $("#help-overlay").hidden = true;
        editor?.focus();
    }

    function insertSnippetAtCursor(snippet) {
        if (!editor) return;
        const sel = editor.getSelection();
        editor.focus();
        const contribution = editor.getContribution("snippetController2");
        if (contribution && typeof contribution.insert === "function") {
            editor.setSelection(sel);
            contribution.insert(snippet);
        } else {
            // Fallback: strip placeholders and insert as plain text.
            const plain = snippet.replace(/\$\{\d+:?([^}]*)\}/g, "$1");
            editor.executeEdits("oxql-help", [{ range: sel, text: plain, forceMoveMarkers: true }]);
        }
    }

    // ── Execute ──────────────────────────────────────────────────────────
    /** The active tab's document, with the entity input applied; null (with the status set) when it is not JSON. */
    function activeDocument() {
        syncActiveFromEditor();
        const tab = tabs.find(t => t.id === activeId);
        if (!tab) return null;

        let body;
        try {
            body = JSON.parse(tab.content);
        } catch (e) {
            setStatus("Invalid JSON: " + e.message, "err");
            renderResult({ error: "Invalid JSON", detail: e.message });
            return null;
        }

        // A batch document carries its own entity types.
        if (!Array.isArray(body?.queries)) {
            // Allow the entity input to override / supply entityType
            if (entityInput.value.trim()) {
                body.entityType = entityInput.value.trim();
            } else if (body.entityType) {
                entityInput.value = body.entityType;
            }
            tab.entityType = body.entityType || "";
        }
        saveTabs();
        return body;
    }

    async function post(path, body) {
        const started = performance.now();
        const res = await fetch(API + path, {
            method: "POST",
            headers: apiHeaders(true),
            body: JSON.stringify(body)
        });
        const elapsed = Math.round(performance.now() - started);
        const text = await res.text();
        let json;
        try { json = JSON.parse(text); } catch { json = text; }
        return { res, json, elapsed };
    }

    function describeOutcome(json) {
        if (json && Array.isArray(json.items)) {
            const info = json.pageInfo || {};
            let s = `${json.items.length} item(s)`;
            if (info.totalCount !== undefined && info.totalCount !== null) s += ` · total ${info.totalCount}${info.totalCountCapped ? "+ (capped)" : ""}`;
            if (info.hasNextPage) s += " · more";
            if (Array.isArray(json.diagnostics) && json.diagnostics.length) s += ` · ${json.diagnostics.length} diagnostic(s)`;
            return s;
        }
        if (json && json.type && Array.isArray(json.errors)) {
            return `${json.type}: ${json.errors.map(e => e.code).join(", ")}`;
        }
        return "";
    }

    async function runQuery() {
        const body = activeDocument();
        if (!body) return;

        const isBatch = Array.isArray(body.queries);
        setStatus(isBatch ? "Running batch…" : "Running…", "");

        try {
            const { res, json, elapsed } = await post(isBatch ? "/batch" : "/query", body);

            if (res.ok) {
                if (isBatch) {
                    const results = Array.isArray(json?.results) ? json.results : [];
                    const refused = results.filter(r => r && r.type && !Array.isArray(r.items)).length;
                    setStatus(`200 OK · ${elapsed} ms · batch of ${results.length}` + (refused ? ` · ${refused} refused` : ""), refused ? "err" : "ok");
                } else {
                    setStatus(`200 OK · ${elapsed} ms · ` + describeOutcome(json), "ok");
                }
            } else {
                setStatus(`${res.status} ${res.statusText} · ${elapsed} ms · ` + describeOutcome(json), "err");
            }
            renderResult(json);
        } catch (e) {
            setStatus("Request failed: " + e.message, "err");
            renderResult({ error: "Request failed", detail: e.message });
        }
    }

    /** Sends every tab's query in one batch and shows the results labelled by tab. */
    async function batchTabs() {
        syncActiveFromEditor();
        const queries = [];
        const labels = [];
        const skipped = [];

        for (const tab of tabs) {
            let body;
            try { body = JSON.parse(tab.content); } catch { skipped.push(tab.name); continue; }
            if (Array.isArray(body?.queries)) {
                // A batch tab contributes its queries.
                body.queries.forEach((q, i) => { queries.push(q); labels.push(`${tab.name} #${i + 1}`); });
            } else if (body && body.entityType) {
                queries.push(body);
                labels.push(tab.name);
            } else {
                skipped.push(tab.name);
            }
        }

        if (!queries.length) {
            setStatus("No runnable tab (each needs an entityType).", "err");
            return;
        }

        setStatus(`Running ${queries.length} tab(s) as one batch…`, "");
        try {
            const { res, json, elapsed } = await post("/batch", { queries });
            if (res.ok && Array.isArray(json?.results)) {
                const labelled = json.results.map((result, i) => ({ tab: labels[i], result }));
                const refused = json.results.filter(r => r && r.type && !Array.isArray(r.items)).length;
                setStatus(`200 OK · ${elapsed} ms · ${queries.length} in one round trip` + (refused ? ` · ${refused} refused` : "") + (skipped.length ? ` · skipped: ${skipped.join(", ")}` : ""), refused ? "err" : "ok");
                renderResult({ results: labelled });
            } else {
                setStatus(`${res.status} ${res.statusText} · ${elapsed} ms · ` + describeOutcome(json), "err");
                renderResult(json);
            }
        } catch (e) {
            setStatus("Request failed: " + e.message, "err");
            renderResult({ error: "Request failed", detail: e.message });
        }
    }

    // ── Explain ──────────────────────────────────────────────────────────
    async function explainQuery() {
        const body = activeDocument();
        if (!body) return;
        if (Array.isArray(body.queries)) {
            setStatus("Explain takes one query, not a batch.", "err");
            return;
        }

        setStatus("Explaining…", "");

        try {
            const { res, json, elapsed } = await post("/explain", body);

            if (res.ok && json && Array.isArray(json.stages)) {
                setStatus(`Explain · ${elapsed} ms · ${json.stages.length} stage(s)` + (json.count ? " · count" : "") + (Array.isArray(json.advisory) ? ` · ${json.advisory.length} advisory line(s)` : ""), "explain");
                renderExplain(json);
            } else if (res.status === 404) {
                setStatus("404 · explain is not enabled on this host (OxQL:Explain:Enabled)", "err");
                renderResult(json);
            } else {
                setStatus(`${res.status} ${res.statusText} · ${elapsed} ms · ` + describeOutcome(json), "err");
                renderResult(json);
            }
        } catch (e) {
            setStatus("Request failed: " + e.message, "err");
            renderResult({ error: "Request failed", detail: e.message });
        }
    }

    function explainHeader(text) {
        const header = document.createElement("div");
        header.className = "explain-header";
        header.textContent = text;
        return header;
    }

    function explainStage(label, value, index, collapsed) {
        const wrap = document.createElement("div");
        wrap.className = "explain-stage" + (collapsed ? " collapsed" : "");

        const stageHeader = document.createElement("div");
        stageHeader.className = "explain-stage-header";

        const num = document.createElement("span");
        num.className = "explain-stage-num";
        num.textContent = index === null ? "·" : String(index + 1);

        const op = document.createElement("span");
        op.className = "explain-stage-op";
        op.textContent = label;

        const toggle = document.createElement("span");
        toggle.className = "explain-stage-toggle";
        toggle.textContent = collapsed ? "▸" : "▾";

        stageHeader.append(num, op, toggle);
        wrap.appendChild(stageHeader);

        const body = document.createElement("pre");
        body.className = "explain-stage-body";
        body.innerHTML = highlightJson(JSON.stringify(value, null, 2));
        wrap.appendChild(body);

        stageHeader.addEventListener("click", () => {
            const isCollapsed = wrap.classList.toggle("collapsed");
            toggle.textContent = isCollapsed ? "▸" : "▾";
        });

        return wrap;
    }

    function stageLabel(stage) {
        return (typeof stage === "object" && stage !== null) ? (Object.keys(stage)[0] || "stage") : "stage";
    }

    /** The v2 explain: the emitted stages, the count pipeline, the index advisory, the bound form and the diagnostics. */
    function renderExplain(explain) {
        const container = resultsEl;
        container.innerHTML = "";

        container.appendChild(explainHeader(`Page pipeline · ${explain.stages.length} stage${explain.stages.length !== 1 ? "s" : ""}`));
        explain.stages.forEach((stage, i) => container.appendChild(explainStage(stageLabel(stage), stage, i, false)));

        if (Array.isArray(explain.count)) {
            container.appendChild(explainHeader(`Count pipeline · ${explain.count.length} stage${explain.count.length !== 1 ? "s" : ""}`));
            explain.count.forEach((stage, i) => container.appendChild(explainStage(stageLabel(stage), stage, i, true)));
        }

        if (Array.isArray(explain.advisory)) {
            container.appendChild(explainHeader("Index advisory"));
            const table = document.createElement("table");
            table.className = "explain-advisory";
            table.innerHTML = "<thead><tr><th>field</th><th>used</th><th>index</th><th>note</th></tr></thead>";
            const tbody = document.createElement("tbody");
            for (const entry of explain.advisory) {
                const tr = document.createElement("tr");
                const used = entry.used === true ? "yes" : entry.used === false ? "no" : "?";
                tr.innerHTML =
                    `<td>${escHtml(entry.field ?? "")}</td>` +
                    `<td class="used-${entry.used === true ? "true" : entry.used === false ? "false" : "null"}">${used}</td>` +
                    `<td>${escHtml(entry.index ?? "")}</td>` +
                    `<td class="note">${escHtml(entry.note ?? "")}</td>`;
                tbody.appendChild(tr);
            }
            table.appendChild(tbody);
            container.appendChild(table);
        } else {
            container.appendChild(explainHeader("Index advisory · not available on this host"));
        }

        if (Array.isArray(explain.diagnostics) && explain.diagnostics.length) {
            container.appendChild(explainHeader("Diagnostics"));
            container.appendChild(explainStage("diagnostics", explain.diagnostics, null, false));
        }

        container.appendChild(explainHeader("Bound pipeline"));
        container.appendChild(explainStage("bound", explain.bound, null, true));
    }

    function setStatus(text, cls) {
        statusEl.textContent = text;
        statusEl.className = "status" + (cls ? " " + cls : "");
    }

    function renderResult(value) {
        const json = typeof value === "string" ? value : JSON.stringify(value, null, 2);
        resultsEl.innerHTML = highlightJson(json);
    }

    function highlightJson(json) {
        if (typeof json !== "string") json = JSON.stringify(json, null, 2);
        const esc = json
            .replace(/&/g, "&amp;")
            .replace(/</g, "&lt;")
            .replace(/>/g, "&gt;");
        return esc.replace(
            /("(\\u[a-zA-Z0-9]{4}|\\[^u]|[^\\"])*"(\s*:)?|\b(true|false)\b|\bnull\b|-?\d+(?:\.\d+)?(?:[eE][+\-]?\d+)?)/g,
            (match) => {
                let cls = "tok-num";
                if (/^"/.test(match)) {
                    cls = /:$/.test(match) ? "tok-key" : "tok-str";
                } else if (/true|false/.test(match)) {
                    cls = "tok-bool";
                } else if (/null/.test(match)) {
                    cls = "tok-null";
                }
                return `<span class="${cls}">${match}</span>`;
            }
        );
    }

    // ── Engine badge (GET /health) ───────────────────────────────────────
    async function loadHealth() {
        const badge = $("#engine-badge");
        if (!badge) return;
        try {
            const res = await fetch(API + "/health", { headers: apiHeaders(false) });
            const json = await res.json();
            const caps = Array.isArray(json?.capabilities) ? json.capabilities : [];
            badge.textContent = `engine ${json?.engine?.version || "?"} · contract ${json?.engine?.contract ?? "?"}`;
            badge.title = "capabilities: " + (caps.join(", ") || "none");
            badge.className = "engine-badge " + (res.ok ? "ok" : "err");
        } catch {
            badge.textContent = "engine unreachable";
            badge.className = "engine-badge err";
        }
    }

    // ── Entity explorer (GET /schema, GET /schema/addons) ────────────────
    /**
     * Resolves one schema descriptor into an explorer node. Objects and enums point into the
     * document's type pool by "#/types/<id>"; arrays carry `of`, dictionaries `value`.
     * Depth is bounded because the pool has cycles.
     */
    function resolveDescriptor(descriptor, pool, depth, seen) {
        const node = {
            name: descriptor.name,
            kind: descriptor.kind || "unknown",
            nullable: !!descriptor.nullable,
            displayName: descriptor.displayName,
            references: descriptor.references,
            typeId: descriptor.type ? String(descriptor.type).replace(/^#\/types\//, "") : null,
            isAddon: !!descriptor.__addon,
            properties: null,
            items: null,
            enumValues: Array.isArray(descriptor.values) ? descriptor.values.map(v => ({ name: v.label ?? v.name ?? v.value, value: v.value })) : null
        };

        if (node.kind === "object" && node.typeId && pool[node.typeId]) {
            if (depth < 4 && !seen.has(node.typeId)) {
                const next = new Set(seen); next.add(node.typeId);
                node.properties = (pool[node.typeId].properties || []).map(p => resolveDescriptor(p, pool, depth + 1, next));
            }
        } else if (node.kind === "enum" && node.typeId && pool[node.typeId]) {
            node.enumValues = pool[node.typeId].values || [];
        } else if (node.kind === "array" && descriptor.of) {
            node.items = resolveDescriptor({ name: "[]", ...descriptor.of }, pool, depth, seen);
        } else if (node.kind === "dictionary" && descriptor.value) {
            node.items = resolveDescriptor({ name: "*", ...descriptor.value }, pool, depth, seen);
        }
        return node;
    }

    /** The entities of a schema document as explorer entries. */
    function entitiesOf(doc) {
        const pool = doc?.types || {};
        const out = [];
        for (const [id, type] of Object.entries(pool)) {
            if (!type || !type.entity) continue;
            const seen = new Set([id]);
            out.push({
                typeName: id,
                displayName: type.displayName,
                extendable: !!type.extendable,
                key: Array.isArray(type.key) ? type.key : [],
                display: type.display,
                aliases: (type.aliases || []).filter(a => !String(a).startsWith("$")),
                properties: (type.properties || []).map(p => resolveDescriptor(p, pool, 0, seen))
            });
        }
        out.sort((a, b) => a.typeName.localeCompare(b.typeName));
        return out;
    }

    /**
     * Slots the organisation's addon definitions (GET /schema/addons) into the schema document
     * itself, so every consumer of the document sees them as ordinary members. The endpoint
     * answers { "<entityId>": [ <property descriptor>, … ] }: each entry is a schema property
     * descriptor exactly as the document's `properties` carry them (name = the definition path,
     * kind, nullable, displayName, description; a closed value list inline as `values`), and
     * the list is the set of properties of the entity's `addon` member. Here the entity's
     * `addon` descriptor is retargeted from an untyped dictionary to an object whose pooled
     * type "<entityId>.addon" holds the descriptors, and paths read `addon.<name>` like any
     * nested member. A host without the endpoint keeps the document as is.
     */
    function applyAddons(doc, addons) {
        if (!doc?.types || !addons || typeof addons !== "object" || Array.isArray(addons)) return;
        for (const [entityId, list] of Object.entries(addons)) {
            const entity = doc.types[entityId];
            if (!entity || !Array.isArray(entity.properties) || !Array.isArray(list)) continue;
            const bag = entity.properties.find(p => p && p.name === "addon");
            if (!bag) continue;
            const poolId = entityId + ".addon";
            doc.types[poolId] = {
                properties: list
                    .filter(d => d && d.name && !d.retired)
                    .map(d => ({ nullable: true, ...d, __addon: true }))
            };
            bag.kind = "object";
            bag.type = "#/types/" + poolId;
            delete bag.value;
        }
    }

    async function loadTypes() {
        const list = $("#types-list");
        list.innerHTML = `<div class="empty">Loading entities…</div>`;
        typesCache = [];

        if (!SCHEMA) {
            list.innerHTML = `<div class="empty">No schema endpoint configured.</div>`;
            return;
        }

        try {
            const headers = apiHeaders(false);
            const res = await fetch(SCHEMA, { headers });
            if (!res.ok) {
                list.innerHTML = `<div class="empty">No schema on this host (GET ${escHtml(SCHEMA)} → ${res.status}). Type the entityType by hand.</div>`;
                return;
            }
            const doc = await res.json();

            // Addon definitions are per organisation and live beside the schema; they slot
            // into the document before it is read, so nothing below knows they were separate.
            try {
                const addonRes = await fetch(SCHEMA + "/addons", { headers });
                if (addonRes.ok) applyAddons(doc, await addonRes.json());
            } catch { /* no addon endpoint on this host */ }

            const entities = entitiesOf(doc);

            if (!entities.length) {
                list.innerHTML = `<div class="empty">The schema declares no entities.</div>`;
                return;
            }
            typesCache = entities;
            list.innerHTML = "";
            for (const t of entities) list.appendChild(renderType(t));
        } catch (e) {
            list.innerHTML = `<div class="empty">Error: ${escHtml(e.message)}</div>`;
        }
    }

    function renderType(t) {
        const node = document.createElement("div");
        node.className = "type-node";

        const header = document.createElement("div");
        header.className = "type-header";

        const caret = document.createElement("span");
        caret.className = "caret open";
        caret.textContent = "▶";

        const name = document.createElement("span");
        name.className = "type-name";
        name.textContent = t.typeName;
        name.title = "Click to start a query on this entity";

        const coll = document.createElement("span");
        coll.className = "collection";
        coll.textContent = t.displayName ? "· " + t.displayName : "";

        header.append(caret, name, coll);
        node.appendChild(header);

        const facts = document.createElement("div");
        facts.className = "type-facts";
        const bits = [];
        if (t.key.length) bits.push("key " + t.key.join(", "));
        if (t.display) bits.push("display " + t.display);
        if (t.extendable) bits.push("extendable");
        if (t.aliases.length) bits.push("retired ids " + t.aliases.join(", "));
        facts.textContent = bits.join(" · ");
        node.appendChild(facts);

        const children = document.createElement("div");
        children.className = "prop-children";
        (t.properties || []).forEach(p => children.appendChild(renderProp(p, "")));
        node.appendChild(children);

        header.addEventListener("click", () => {
            const open = children.style.display !== "none";
            children.style.display = open ? "none" : "";
            facts.style.display = open ? "none" : "";
            caret.classList.toggle("open", !open);
        });

        // Clicking the type name inserts an entityType skeleton
        name.addEventListener("click", (e) => {
            e.stopPropagation();
            insertSkeleton(t.typeName);
        });

        return node;
    }

    function renderProp(p, prefix) {
        const wrap = document.createElement("div");

        const row = document.createElement("div");
        row.className = "prop";

        const path = prefix ? `${prefix}.${p.name}` : p.name;
        const childProps = p.kind === "object"
            ? (p.properties || [])
            : (p.items?.properties || (p.items ? [p.items] : []));
        const hasChildren = childProps.length > 0;

        const toggle = document.createElement("span");
        toggle.className = "toggle";
        toggle.textContent = hasChildren ? "▶" : "";

        const pname = document.createElement("span");
        pname.className = "pname" + (p.isAddon ? " addon" : "");
        pname.textContent = p.name;

        const pkind = document.createElement("span");
        pkind.className = "pkind" + (p.kind === "array" ? " array" : "");
        pkind.textContent = ":" + kindLabel(p);
        if (p.enumValues && p.enumValues.length) {
            pkind.title = p.enumValues.map(v => `${v.name} = ${v.value}`).join("\n");
        }

        const nullable = document.createElement("span");
        nullable.className = "nullable";
        nullable.textContent = p.nullable ? "?" : "";

        row.append(toggle, pname, pkind, nullable);

        if (p.references && p.references.entity) {
            const ref = document.createElement("span");
            ref.className = "pref";
            ref.textContent = "→ " + p.references.entity;
            ref.title = "declared reference: resolve / lookup follow it";
            row.appendChild(ref);
        }

        wrap.appendChild(row);

        // Insert the wire path into the editor on click
        row.addEventListener("click", (e) => {
            e.stopPropagation();
            insertAtCursor(`"${path}"`);
        });

        if (hasChildren) {
            const kids = document.createElement("div");
            kids.className = "prop-children";
            kids.style.display = "none";

            const childPrefix = p.kind === "object" ? path : path;
            childProps.forEach(cp => kids.appendChild(renderProp(cp, cp.name === "[]" || cp.name === "*" ? path : childPrefix)));
            wrap.appendChild(kids);

            toggle.style.cursor = "pointer";
            const flip = (e) => {
                e.stopPropagation();
                const open = kids.style.display !== "none";
                kids.style.display = open ? "none" : "";
                toggle.textContent = open ? "▶" : "▼";
            };
            row.addEventListener("dblclick", flip);
            toggle.addEventListener("click", flip);
        }

        return wrap;
    }

    function kindLabel(p) {
        if (p.kind === "array") {
            const inner = p.items ? kindLabel(p.items) : "unknown";
            return inner + "[]";
        }
        if (p.kind === "dictionary") {
            const v = p.items ? kindLabel(p.items) : "unknown";
            return `map<${v}>`;
        }
        if (p.kind === "enum" && p.typeId) return "enum " + p.typeId;
        if (p.kind === "object" && p.typeId) return p.typeId;
        return p.kind;
    }

    function insertSkeleton(typeName) {
        const skeleton = {
            entityType: typeName,
            pipeline: [
                { match: {} },
                { page: { limit: 25 } }
            ]
        };
        if (editor) {
            editor.setValue(JSON.stringify(skeleton, null, 2));
            entityInput.value = typeName;
            const tab = tabs.find(t => t.id === activeId);
            if (tab) { tab.entityType = typeName; }
            saveTabs();
        }
    }

    function insertAtCursor(text) {
        if (!editor) return;
        const sel = editor.getSelection();
        editor.executeEdits("oxql-insert", [{ range: sel, text, forceMoveMarkers: true }]);
        editor.focus();
    }

    function formatDocument() {
        editor?.getAction("editor.action.formatDocument")?.run();
    }

    // ── Query wizard ─────────────────────────────────────────────────────
    const WIZARD_STEPS = [
        { key: "entity",  title: "Entity",     label: "Entity" },
        { key: "filter",  title: "Filter",     label: "Filter" },
        { key: "project", title: "Projection", label: "Fields" },
        { key: "sort",    title: "Sort",       label: "Sort" },
        { key: "page",    title: "Paging",     label: "Paging" },
        { key: "review",  title: "Review",     label: "Review" }
    ];

    const OPERATORS = (window.OxQLLang?.operatorSnippets || [])
        .filter(o => o.label !== "ignoreCase")
        .map(o => ({ value: o.label, detail: o.detail }));
    const VALUE_TYPES = [
        { value: "auto",    label: "auto" },
        { value: "string",  label: "string" },
        { value: "number",  label: "number" },
        { value: "bool",    label: "bool" },
        { value: "null",    label: "null" },
        { value: "$var",    label: "$var" }
    ];

    // Live state for the currently-open wizard.
    let wiz = null;

    function defaultWizardState() {
        return {
            step: 0,
            entityType: (tabs.find(t => t.id === activeId)?.entityType) || "",
            filters: [],   // { path, op, value, type }
            project: {},   // path -> true (included). Empty => project all.
            sort: [],      // { path, dir }
            limit: 25,
            includeTotalCount: false
        };
    }

    // Flatten an entity's property tree into dot-notation wire paths (bounded depth).
    function entityFieldPaths(typeName) {
        const t = typesCache.find(x => x.typeName === typeName);
        if (!t) return [];
        const out = [];
        const walk = (props, prefix, depth) => {
            if (!props || depth > 3) return;
            for (const p of props) {
                const path = prefix ? `${prefix}.${p.name}` : p.name;
                out.push(path);
                if (p.kind === "object" && p.properties?.length) {
                    walk(p.properties, path, depth + 1);
                } else if ((p.kind === "array" || p.kind === "dictionary") && p.items?.properties?.length) {
                    walk(p.items.properties, path, depth + 1);
                }
            }
        };
        walk(t.properties, "", 0);
        return out;
    }

    function openWizard() {
        wiz = defaultWizardState();
        $("#wizard-overlay").hidden = false;
        $("#wizard").hidden = false;
        renderWizard();
    }

    function closeWizard() {
        $("#wizard").hidden = true;
        $("#wizard-overlay").hidden = true;
        wiz = null;
        editor?.focus();
    }

    function wizardBack() {
        if (!wiz) return;
        if (wiz.step === 0) { closeWizard(); return; }
        wiz.step--;
        renderWizard();
    }

    function wizardNext() {
        if (!wiz) return;
        // Entity is required to proceed past step 0.
        if (wiz.step === 0 && !wiz.entityType.trim()) {
            $("#wizard-hint").textContent = "Pick or type an entity to continue.";
            return;
        }
        if (wiz.step === WIZARD_STEPS.length - 1) {
            finishWizard();
            return;
        }
        wiz.step++;
        renderWizard();
    }

    function renderWizard() {
        renderWizardSteps();
        const body = $("#wizard-body");
        body.innerHTML = "";
        const key = WIZARD_STEPS[wiz.step].key;
        ({
            entity:  renderStepEntity,
            filter:  renderStepFilter,
            project: renderStepProject,
            sort:    renderStepSort,
            page:    renderStepPage,
            review:  renderStepReview
        })[key](body);

        $("#wizard-back").textContent = wiz.step === 0 ? "Cancel" : "← Back";
        $("#wizard-next").textContent = wiz.step === WIZARD_STEPS.length - 1 ? "✓ Create query" : "Next →";
        $("#wizard-hint").textContent = "";
    }

    function renderWizardSteps() {
        const host = $("#wizard-steps");
        host.innerHTML = "";
        WIZARD_STEPS.forEach((s, i) => {
            const pill = document.createElement("span");
            pill.className = "wizard-step-pill"
                + (i === wiz.step ? " active" : "")
                + (i < wiz.step ? " done" : "");
            const num = document.createElement("span");
            num.className = "num";
            num.textContent = i < wiz.step ? "✓" : String(i + 1);
            const lbl = document.createElement("span");
            lbl.textContent = s.label;
            pill.append(num, lbl);
            // Allow jumping back to any already-visited step.
            if (i <= wiz.step) {
                pill.style.cursor = "pointer";
                pill.addEventListener("click", () => { wiz.step = i; renderWizard(); });
            }
            host.appendChild(pill);
        });
    }

    // Shared datalist of the selected entity's field paths.
    function fieldDatalist() {
        const paths = entityFieldPaths(wiz.entityType);
        if (!paths.length) return { html: "", listId: "" };
        const listId = "wiz-fields";
        const opts = paths.map(p => `<option value="${escAttr(p)}"></option>`).join("");
        return { html: `<datalist id="${listId}">${opts}</datalist>`, listId };
    }

    // ── Step 1: Entity ───────────────────────────────────────────────────
    function renderStepEntity(body) {
        const options = typesCache.map(t =>
            `<option value="${escAttr(t.typeName)}">${escHtml(t.typeName)}${t.displayName ? " · " + escHtml(t.displayName) : ""}</option>`
        ).join("");

        body.innerHTML = `
            <h3>Choose an entity</h3>
            <div class="step-desc">Select the entity to query. Its id becomes the query's <code>entityType</code>, matched exactly.</div>
            <div class="wizard-field">
                <label for="wiz-entity-select">Entities of this host</label>
                <select id="wiz-entity-select">
                    <option value="">— select an entity —</option>
                    ${options}
                </select>
            </div>
            <div class="wizard-field">
                <label for="wiz-entity-text">Or type an entity id</label>
                <input type="text" id="wiz-entity-text" placeholder="vehicle.vehicle" spellcheck="false" value="${escAttr(wiz.entityType)}" />
            </div>`;

        const select = $("#wiz-entity-select");
        const text = $("#wiz-entity-text");
        if (typesCache.some(t => t.typeName === wiz.entityType)) select.value = wiz.entityType;

        select.addEventListener("change", () => {
            wiz.entityType = select.value;
            text.value = select.value;
        });
        text.addEventListener("input", () => {
            wiz.entityType = text.value.trim();
            select.value = typesCache.some(t => t.typeName === wiz.entityType) ? wiz.entityType : "";
        });
    }

    // ── Step 2: Filter (match) ───────────────────────────────────────────
    function renderStepFilter(body) {
        const dl = fieldDatalist();
        body.innerHTML = `
            <h3>Filter rows</h3>
            <div class="step-desc">Add field conditions on wire paths. All rows are combined with logical <strong>AND</strong>. Leave empty to match everything.</div>
            <div id="wiz-filter-rows"></div>
            <button class="wizard-add-row" id="wiz-add-filter">＋ Add condition</button>
            ${dl.html}`;

        const rows = $("#wiz-filter-rows");
        const draw = () => {
            rows.innerHTML = "";
            if (!wiz.filters.length) {
                rows.innerHTML = `<div class="wizard-empty">No conditions — the query will match every row of the organisation.</div>`;
            }
            wiz.filters.forEach((f, i) => rows.appendChild(filterRow(f, i, dl.listId)));
        };

        $("#wiz-add-filter").addEventListener("click", () => {
            wiz.filters.push({ path: "", op: "eq", value: "", type: "auto" });
            draw();
        });
        draw();
    }

    function filterRow(f, i, listId) {
        const row = document.createElement("div");
        row.className = "wizard-grid";

        const path = document.createElement("input");
        path.type = "text";
        path.placeholder = "field.path";
        path.value = f.path;
        if (listId) path.setAttribute("list", listId);
        path.addEventListener("input", () => { f.path = path.value.trim(); });

        const op = document.createElement("select");
        op.innerHTML = OPERATORS.map(o =>
            `<option value="${o.value}"${o.value === f.op ? " selected" : ""}>${o.value}</option>`
        ).join("");
        op.addEventListener("change", () => { f.op = op.value; });

        const value = document.createElement("input");
        value.type = "text";
        value.placeholder = "value";
        value.value = f.value;
        value.addEventListener("input", () => { f.value = value.value; });

        const type = document.createElement("select");
        type.innerHTML = VALUE_TYPES.map(v =>
            `<option value="${v.value}"${v.value === f.type ? " selected" : ""}>${v.label}</option>`
        ).join("");
        type.title = "Value type";
        type.addEventListener("change", () => { f.type = type.value; });

        const remove = document.createElement("button");
        remove.className = "icon wizard-row-remove";
        remove.textContent = "✕";
        remove.title = "Remove condition";
        remove.addEventListener("click", () => {
            wiz.filters.splice(i, 1);
            renderWizard();
        });

        // Grid columns: path | op | value | type+remove wrapper
        const tail = document.createElement("div");
        tail.className = "wizard-inline";
        tail.append(type, remove);

        row.append(path, op, value, tail);
        return row;
    }

    // ── Step 3: Projection ───────────────────────────────────────────────
    function renderStepProject(body) {
        const paths = entityFieldPaths(wiz.entityType);
        body.innerHTML = `
            <h3>Choose output fields</h3>
            <div class="step-desc">Select the fields to include (<code>id</code> is always kept). Selecting none returns the full row.</div>`;

        if (!paths.length) {
            const note = document.createElement("div");
            note.className = "wizard-empty";
            note.textContent = "No field metadata for this entity — projection will be skipped (full row returned).";
            body.appendChild(note);
            return;
        }

        const bar = document.createElement("div");
        bar.className = "wizard-inline";
        bar.style.marginBottom = "10px";
        const all = document.createElement("button");
        all.textContent = "Select all";
        const none = document.createElement("button");
        none.textContent = "Clear";
        bar.append(all, none);
        body.appendChild(bar);

        const grid = document.createElement("div");
        grid.className = "wizard-checks";
        body.appendChild(grid);

        const draw = () => {
            grid.innerHTML = "";
            paths.forEach(p => {
                const lbl = document.createElement("label");
                lbl.className = "wizard-check";
                const cb = document.createElement("input");
                cb.type = "checkbox";
                cb.checked = !!wiz.project[p];
                cb.addEventListener("change", () => {
                    if (cb.checked) wiz.project[p] = true;
                    else delete wiz.project[p];
                });
                const span = document.createElement("span");
                span.textContent = p;
                lbl.append(cb, span);
                grid.appendChild(lbl);
            });
        };
        all.addEventListener("click", () => { paths.forEach(p => wiz.project[p] = true); draw(); });
        none.addEventListener("click", () => { wiz.project = {}; draw(); });
        draw();
    }

    // ── Step 4: Sort ─────────────────────────────────────────────────────
    function renderStepSort(body) {
        const dl = fieldDatalist();
        body.innerHTML = `
            <h3>Order rows</h3>
            <div class="step-desc">Sort by one or more scalar fields. The engine appends the <code>id</code> tie-breaker on a root shape.</div>
            <div id="wiz-sort-rows"></div>
            <button class="wizard-add-row" id="wiz-add-sort">＋ Add sort field</button>
            ${dl.html}`;

        const rows = $("#wiz-sort-rows");
        const draw = () => {
            rows.innerHTML = "";
            if (!wiz.sort.length) {
                rows.innerHTML = `<div class="wizard-empty">No sort — rows come in key order.</div>`;
            }
            wiz.sort.forEach((s, i) => rows.appendChild(sortRow(s, i, dl.listId)));
        };
        $("#wiz-add-sort").addEventListener("click", () => {
            wiz.sort.push({ path: "", dir: "desc" });
            draw();
        });
        draw();
    }

    function sortRow(s, i, listId) {
        const row = document.createElement("div");
        row.className = "wizard-grid sort-grid";

        const path = document.createElement("input");
        path.type = "text";
        path.placeholder = "field.path";
        path.value = s.path;
        if (listId) path.setAttribute("list", listId);
        path.addEventListener("input", () => { s.path = path.value.trim(); });

        const dir = document.createElement("select");
        dir.innerHTML = `
            <option value="desc"${s.dir === "desc" ? " selected" : ""}>desc</option>
            <option value="asc"${s.dir === "asc" ? " selected" : ""}>asc</option>`;
        dir.addEventListener("change", () => { s.dir = dir.value; });

        const remove = document.createElement("button");
        remove.className = "icon wizard-row-remove";
        remove.textContent = "✕";
        remove.title = "Remove sort field";
        remove.addEventListener("click", () => {
            wiz.sort.splice(i, 1);
            renderWizard();
        });

        row.append(path, dir, remove);
        return row;
    }

    // ── Step 5: Paging ───────────────────────────────────────────────────
    function renderStepPage(body) {
        body.innerHTML = `
            <h3>Pagination</h3>
            <div class="step-desc">Control the page size and whether the server counts the matching rows (up to its cap).</div>
            <div class="wizard-field">
                <label for="wiz-limit">Page size (limit)</label>
                <input type="number" id="wiz-limit" min="1" max="500" value="${Number(wiz.limit) || 25}" />
            </div>
            <div class="wizard-field">
                <label class="wizard-check">
                    <input type="checkbox" id="wiz-total" ${wiz.includeTotalCount ? "checked" : ""} />
                    <span>Include total matching count</span>
                </label>
            </div>`;

        $("#wiz-limit").addEventListener("input", (e) => {
            const n = parseInt(e.target.value, 10);
            wiz.limit = Number.isFinite(n) && n > 0 ? n : 25;
        });
        $("#wiz-total").addEventListener("change", (e) => {
            wiz.includeTotalCount = e.target.checked;
        });
    }

    // ── Step 6: Review ───────────────────────────────────────────────────
    function renderStepReview(body) {
        const query = buildWizardQuery();
        const json = JSON.stringify(query, null, 2);
        body.innerHTML = `
            <h3>Review &amp; create</h3>
            <div class="step-desc">This query will open in a new tab. You can keep editing it afterwards.</div>
            <pre class="wizard-review">${escHtml(json)}</pre>`;
    }

    // ── Query assembly ───────────────────────────────────────────────────
    function coerceValue(raw, type) {
        const s = (raw ?? "").toString();
        switch (type) {
            case "string": return s;
            case "number": { const n = Number(s); return Number.isFinite(n) ? n : s; }
            case "bool":   return /^(true|1|yes)$/i.test(s.trim());
            case "null":   return null;
            case "$var":   return { $var: s };
            case "auto":
            default: {
                const t = s.trim();
                if (t === "") return "";
                if (t === "null") return null;
                if (t === "true") return true;
                if (t === "false") return false;
                if (/^-?\d+(\.\d+)?$/.test(t)) return Number(t);
                return s;
            }
        }
    }

    // Convert a "a.b.c" path + value into a nested object for projections.
    function setNested(target, path, value) {
        const parts = path.split(".").filter(Boolean);
        let node = target;
        for (let i = 0; i < parts.length - 1; i++) {
            node[parts[i]] = node[parts[i]] || {};
            node = node[parts[i]];
        }
        node[parts[parts.length - 1]] = value;
    }

    function buildWizardQuery() {
        const query = { entityType: wiz.entityType.trim() };
        const pipeline = [];

        // match
        const conditions = wiz.filters.filter(f => f.path.trim());
        if (conditions.length) {
            const match = {};
            for (const f of conditions) {
                const operand = (f.op === "in" || f.op === "nin")
                    ? String(f.value).split(",").map(v => coerceValue(v.trim(), f.type))
                    : coerceValue(f.value, f.type);
                match[f.path.trim()] = Object.assign(match[f.path.trim()] || {}, { [f.op]: operand });
            }
            pipeline.push({ match });
        }

        // project
        const picked = Object.keys(wiz.project).filter(k => wiz.project[k]);
        if (picked.length) {
            const project = {};
            for (const p of picked) setNested(project, p, 1);
            pipeline.push({ project });
        }

        // sort
        const sorts = wiz.sort.filter(s => s.path.trim());
        if (sorts.length) {
            pipeline.push({ sort: sorts.map(s => ({ [s.path.trim()]: s.dir })) });
        }

        // page
        const page = { limit: Number(wiz.limit) || 25 };
        if (wiz.includeTotalCount) page.includeTotalCount = true;
        pipeline.push({ page });

        query.pipeline = pipeline;
        return query;
    }

    function finishWizard() {
        const query = buildWizardQuery();
        const baseName = (wiz.entityType.split(".").pop() || "Query");
        const name = baseName.charAt(0).toUpperCase() + baseName.slice(1);

        syncActiveFromEditor();
        const tab = makeTab(name, query);
        tabs.push(tab);
        activeId = tab.id;
        saveTabs();
        swapEditorModel();
        entityInput.value = tab.entityType || "";
        renderTabs();
        closeWizard();
        setStatus("Query created from wizard", "");
    }

    // Small HTML-escaping helpers for markup.
    function escHtml(s) {
        return String(s).replace(/[&<>]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;" }[c]));
    }
    function escAttr(s) {
        return String(s).replace(/[&<>"]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
    }

    // ── Wire up UI ───────────────────────────────────────────────────────
    function bindEvents() {
        tabAdd.addEventListener("click", addTab);
        $("#run-btn").addEventListener("click", runQuery);
        $("#batch-btn").addEventListener("click", batchTabs);
        $("#format-btn").addEventListener("click", formatDocument);

        // Explain button — only shown when the host has OxQL:Explain:Enabled = true
        const explainBtn = $("#explain-btn");
        if (cfg.enableExplain) {
            explainBtn.hidden = false;
            explainBtn.addEventListener("click", explainQuery);
        }

        entityInput.addEventListener("change", () => {
            const tab = tabs.find(t => t.id === activeId);
            if (tab) { tab.entityType = entityInput.value.trim(); saveTabs(); }
        });

        contractSelect.addEventListener("change", () => {
            saveContract();
            setStatus(`Contract ${contractSelect.value} on every request` + (contractSelect.value === "1" ? " (storage spelling, v1 type hints; refused once the host switches compat off)" : ""), "");
        });

        tokenInput.addEventListener("input", saveToken);
        $("#token-toggle").addEventListener("click", () => {
            tokenInput.type = tokenInput.type === "password" ? "text" : "password";
        });
        $("#token-clear").addEventListener("click", () => {
            tokenInput.value = "";
            saveToken();
        });

        $("#types-refresh").addEventListener("click", loadTypes);
        $("#copy-result").addEventListener("click", () => {
            navigator.clipboard?.writeText(resultsEl.textContent || "");
        });

        // Help drawer
        $("#help-btn").addEventListener("click", openHelp);
        $("#help-close").addEventListener("click", closeHelp);
        $("#help-overlay").addEventListener("click", closeHelp);
        $("#help-search-input").addEventListener("input", (e) => filterHelp(e.target.value));
        document.addEventListener("keydown", (e) => {
            if (e.key === "Escape" && !$("#wizard").hidden) { closeWizard(); return; }
            if (e.key === "Escape" && !$("#help-drawer").hidden) closeHelp();
            if (e.key === "F1") { e.preventDefault(); openHelp(); }
        });

        // Query wizard
        $("#wizard-btn").addEventListener("click", openWizard);
        $("#wizard-close").addEventListener("click", closeWizard);
        $("#wizard-overlay").addEventListener("click", closeWizard);
        $("#wizard-back").addEventListener("click", wizardBack);
        $("#wizard-next").addEventListener("click", wizardNext);

        // Persist before unload
        window.addEventListener("beforeunload", saveTabs);
    }

    // ── Boot ─────────────────────────────────────────────────────────────
    async function boot() {
        loadTabs();
        loadToken();
        loadContract();
        renderTabs();
        const active = tabs.find(t => t.id === activeId);
        entityInput.value = active?.entityType || "";
        bindEvents();

        // Update hint text to include Explain shortcut when available
        if (cfg.enableExplain) {
            const hint = $(".toolbar .hint");
            if (hint) hint.textContent = "Ctrl+Enter to run · Ctrl+Shift+E to explain · Ctrl+Space for suggestions · saved in your browser";
        }

        await initMonaco();
        setStatus("Ready", "");
        loadHealth();
        loadTypes();
    }

    boot();
})();
