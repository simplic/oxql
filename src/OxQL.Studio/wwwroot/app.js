(function () {
    "use strict";

    // ── Config ───────────────────────────────────────────────────────────
    const cfg = JSON.parse(document.getElementById("oxql-config").textContent);
    const API = cfg.apiBasePath.replace(/\/$/, "");
    const MONACO_BASE = cfg.monacoCdnBase.replace(/\/$/, "");

    const LS_TABS = "oxql.studio.tabs.v1";
    const LS_ACTIVE = "oxql.studio.activeTab.v1";
    const LS_TOKEN = "oxql.studio.bearer.v1";

    const DEFAULT_QUERY = {
        entityType: "vehicle.vehicle",
        pipeline: [
            { match: { MatchCode: { neq: null } } },
            { sort: { createdAt: "desc" } },
            { page: { limit: 25 } }
        ]
    };

    // ── State ────────────────────────────────────────────────────────────
    let tabs = [];          // [{ id, name, entityType, content }]
    let activeId = null;
    let editor = null;
    let monacoModelsByTab = {};   // id -> monaco model
    let typesCache = [];          // last-loaded type descriptors (for the wizard)

    // ── DOM ──────────────────────────────────────────────────────────────
    const $ = (sel) => document.querySelector(sel);
    const tabbar = $("#tabbar");
    const tabAdd = $("#tab-add");
    const tokenInput = $("#token-input");
    const tokenDot = $("#token-dot");
    const entityInput = $("#entity-input");
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

    // ── Token ────────────────────────────────────────────────────────────
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
            typehint: { icon: () => monaco.languages.CompletionItemKind.TypeParameter, sort: "4", tag: "type hint" },
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

    // ── Execute query ────────────────────────────────────────────────────
    async function runQuery() {
        syncActiveFromEditor();
        const tab = tabs.find(t => t.id === activeId);
        if (!tab) return;

        let body;
        try {
            body = JSON.parse(tab.content);
        } catch (e) {
            setStatus("Invalid JSON: " + e.message, "err");
            renderResult({ error: "Invalid JSON", detail: e.message });
            return;
        }

        // Allow the entity input to override / supply entityType
        if (entityInput.value.trim()) {
            body.entityType = entityInput.value.trim();
        } else if (body.entityType) {
            entityInput.value = body.entityType;
        }
        tab.entityType = body.entityType || "";
        saveTabs();

        setStatus("Running…", "");
        const started = performance.now();

        try {
            const headers = { "Content-Type": "application/json" };
            const token = tokenInput.value.trim();
            if (token) headers["Authorization"] = "Bearer " + token;

            const res = await fetch(API + "/query", {
                method: "POST",
                headers,
                body: JSON.stringify(body)
            });

            const elapsed = Math.round(performance.now() - started);
            const text = await res.text();
            let json;
            try { json = JSON.parse(text); } catch { json = text; }

            if (res.ok) {
                const count = json?.items?.length ?? json?.data?.length ?? null;
                setStatus(`200 OK · ${elapsed} ms` + (count !== null ? ` · ${count} item(s)` : ""), "ok");
            } else {
                setStatus(`${res.status} ${res.statusText} · ${elapsed} ms`, "err");
            }
            renderResult(json);
        } catch (e) {
            setStatus("Request failed: " + e.message, "err");
            renderResult({ error: "Request failed", detail: e.message });
        }
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

    // ── Type explorer ────────────────────────────────────────────────────
    async function loadTypes() {
        const list = $("#types-list");
        list.innerHTML = `<div class="empty">Loading types…</div>`;
        try {
            const headers = {};
            const token = tokenInput.value.trim();
            if (token) headers["Authorization"] = "Bearer " + token;

            const res = await fetch(API + "/types", { headers });
            if (!res.ok) {
                list.innerHTML = `<div class="empty">Failed to load types (${res.status}).</div>`;
                return;
            }
            const types = await res.json();
            if (!Array.isArray(types) || types.length === 0) {
                list.innerHTML = `<div class="empty">No registered types.</div>`;
                return;
            }
            typesCache = types;
            list.innerHTML = "";
            for (const t of types) list.appendChild(renderType(t));
        } catch (e) {
            list.innerHTML = `<div class="empty">Error: ${e.message}</div>`;
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

        const coll = document.createElement("span");
        coll.className = "collection";
        coll.textContent = t.collectionName ? "→ " + t.collectionName : "";

        header.append(caret, name, coll);
        node.appendChild(header);

        const children = document.createElement("div");
        children.className = "prop-children";
        (t.properties || []).forEach(p => children.appendChild(renderProp(p)));
        node.appendChild(children);

        header.addEventListener("click", () => {
            const open = children.style.display !== "none";
            children.style.display = open ? "none" : "";
            caret.classList.toggle("open", !open);
        });

        // Clicking the type name inserts an entityType skeleton
        name.addEventListener("click", (e) => {
            e.stopPropagation();
            insertSkeleton(t.typeName);
        });

        return node;
    }

    function renderProp(p) {
        const wrap = document.createElement("div");

        const row = document.createElement("div");
        row.className = "prop";

        const hasChildren = (p.kind === "object" && p.properties?.length)
            || ((p.kind === "array" || p.kind === "dictionary") && p.items);

        const toggle = document.createElement("span");
        toggle.className = "toggle";
        toggle.textContent = hasChildren ? "▶" : "";

        const pname = document.createElement("span");
        pname.className = "pname";
        pname.textContent = p.name;

        const pkind = document.createElement("span");
        pkind.className = "pkind" + (p.kind === "array" ? " array" : "");
        pkind.textContent = ":" + kindLabel(p);

        const nullable = document.createElement("span");
        nullable.className = "nullable";
        nullable.textContent = p.nullable ? "?" : "";

        row.append(toggle, pname, pkind, nullable);
        wrap.appendChild(row);

        // Insert the field path into the editor on click
        row.addEventListener("click", (e) => {
            e.stopPropagation();
            insertAtCursor(`"${p.name}"`);
        });

        if (hasChildren) {
            const kids = document.createElement("div");
            kids.className = "prop-children";
            kids.style.display = "none";

            const childProps = p.kind === "object"
                ? p.properties
                : (p.items?.properties || (p.items ? [p.items] : []));
            childProps.forEach(cp => kids.appendChild(renderProp(cp)));
            wrap.appendChild(kids);

            toggle.style.cursor = "pointer";
            row.addEventListener("dblclick", (e) => {
                e.stopPropagation();
                const open = kids.style.display !== "none";
                kids.style.display = open ? "none" : "";
                toggle.textContent = open ? "▶" : "▼";
            });
            toggle.addEventListener("click", (e) => {
                e.stopPropagation();
                const open = kids.style.display !== "none";
                kids.style.display = open ? "none" : "";
                toggle.textContent = open ? "▶" : "▼";
            });
        }

        return wrap;
    }

    function kindLabel(p) {
        if (p.kind === "array") {
            const inner = p.items ? kindLabel(p.items) : "any";
            return inner + "[]";
        }
        if (p.kind === "dictionary") {
            const v = p.items ? kindLabel(p.items) : "any";
            return `map<${p.keyKind || "string"},${v}>`;
        }
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

    const OPERATORS = (window.OxQLLang?.operatorSnippets || []).map(o => ({ value: o.label, detail: o.detail }));
    const VALUE_TYPES = [
        { value: "auto",    label: "auto" },
        { value: "string",  label: "string" },
        { value: "number",  label: "number" },
        { value: "bool",    label: "bool" },
        { value: "null",    label: "null" },
        { value: "$uuid",   label: "uuid" },
        { value: "$date",   label: "date" },
        { value: "$oid",    label: "objectId" },
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

    // Flatten an entity's property tree into dot-notation paths (bounded depth).
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
            `<option value="${escAttr(t.typeName)}">${escHtml(t.typeName)}${t.collectionName ? " → " + escHtml(t.collectionName) : ""}</option>`
        ).join("");

        body.innerHTML = `
            <h3>Choose an entity</h3>
            <div class="step-desc">Select the collection / entity to query. This becomes the query's <code>entityType</code>.</div>
            <div class="wizard-field">
                <label for="wiz-entity-select">Registered entities</label>
                <select id="wiz-entity-select">
                    <option value="">— select an entity —</option>
                    ${options}
                </select>
            </div>
            <div class="wizard-field">
                <label for="wiz-entity-text">Or type an entity name</label>
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
            <h3>Filter documents</h3>
            <div class="step-desc">Add field conditions. All rows are combined with logical <strong>AND</strong>. Leave empty to match everything.</div>
            <div id="wiz-filter-rows"></div>
            <button class="wizard-add-row" id="wiz-add-filter">＋ Add condition</button>
            ${dl.html}`;

        const rows = $("#wiz-filter-rows");
        const draw = () => {
            rows.innerHTML = "";
            if (!wiz.filters.length) {
                rows.innerHTML = `<div class="wizard-empty">No conditions — the query will match all documents.</div>`;
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
        path.placeholder = "Field.Path";
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
        type.title = "Value type / hint";
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
            <div class="step-desc">Select the fields to include. Selecting none returns the full document.</div>`;

        if (!paths.length) {
            const note = document.createElement("div");
            note.className = "wizard-empty";
            note.textContent = "No field metadata for this entity — projection will be skipped (full document returned).";
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
            <h3>Order results</h3>
            <div class="step-desc">Sort by one or more fields. A tie-breaker on <code>id</code> is applied by the server automatically.</div>
            <div id="wiz-sort-rows"></div>
            <button class="wizard-add-row" id="wiz-add-sort">＋ Add sort field</button>
            ${dl.html}`;

        const rows = $("#wiz-sort-rows");
        const draw = () => {
            rows.innerHTML = "";
            if (!wiz.sort.length) {
                rows.innerHTML = `<div class="wizard-empty">No sort — results use the server's default order.</div>`;
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
        path.placeholder = "Field.Path";
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
            <div class="step-desc">Control the page size and whether the server returns a total count.</div>
            <div class="wizard-field">
                <label for="wiz-limit">Page size (limit)</label>
                <input type="number" id="wiz-limit" min="1" max="1000" value="${Number(wiz.limit) || 25}" />
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
            case "$uuid":  return { $uuid: s };
            case "$date":  return { $date: s };
            case "$oid":   return { $oid: s };
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
                match[f.path.trim()] = { [f.op]: coerceValue(f.value, f.type) };
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

    // Small HTML-escaping helpers for wizard markup.
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
        $("#format-btn").addEventListener("click", formatDocument);

        entityInput.addEventListener("change", () => {
            const tab = tabs.find(t => t.id === activeId);
            if (tab) { tab.entityType = entityInput.value.trim(); saveTabs(); }
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
        renderTabs();
        const active = tabs.find(t => t.id === activeId);
        entityInput.value = active?.entityType || "";
        bindEvents();

        await initMonaco();
        setStatus("Ready", "");
        loadTypes();
    }

    boot();
})();
