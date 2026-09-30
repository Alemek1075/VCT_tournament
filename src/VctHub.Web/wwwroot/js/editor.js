// Block editor: C12 (blocks + markdown shortcuts + localStorage), F3 (drag and drop),
// C13 (SignalR sync between devices), C14 (remote edits are highlighted / animated, live cursors).
(function () {
    const cfg = window.EDITOR;
    const $list = document.getElementById('blocks');
    const $title = document.getElementById('doc-title');
    const $status = document.getElementById('sync-status');
    const $peers = document.getElementById('peers');
    const KEY = cfg.docId ? `vct.doc.${cfg.docId}` : 'vct.doc.local';

    const TYPES = { p: 'Text', h1: 'Heading 1', h2: 'Heading 2', h3: 'Heading 3', ul: 'Bullet', ol: 'Numbered', todo: 'To-do', quote: 'Quote', code: 'Code', hr: 'Divider' };
    // markdown typed at the start of a paragraph turns it into another block type
    const SHORTCUTS = [[/^###\s$/, 'h3'], [/^##\s$/, 'h2'], [/^#\s$/, 'h1'], [/^[-*]\s$/, 'ul'], [/^1[.)]\s$/, 'ol'],
                       [/^\[\s?\]\s$/, 'todo'], [/^>\s$/, 'quote'], [/^```$/, 'code'], [/^---$/, 'hr']];
    const uid = () => Math.random().toString(36).slice(2, 12);

    let state = { title: cfg.title, blocks: [] };
    let hub = null, me = null, connected = false;

    // ---------- persistence (C12): every change goes to localStorage, so closing the browser loses nothing
    function saveLocal() {
        try { localStorage.setItem(KEY, JSON.stringify({ ...state, savedAt: Date.now() })); } catch { /* private mode */ }
        $status.dataset.local = new Date().toLocaleTimeString();
        paintStatus();
    }
    function loadLocal() {
        try { return JSON.parse(localStorage.getItem(KEY) || 'null'); } catch { return null; }
    }

    // ---------- rendering
    function rowFor(b) {
        const row = document.createElement('div');
        row.className = 'blk';
        row.dataset.id = b.id;
        row.dataset.type = b.type;
        row.innerHTML = `<span class="handle" title="Drag to move" aria-hidden="true">⠿</span>`;
        if (b.type === 'todo') {
            const cb = document.createElement('input');
            cb.type = 'checkbox'; cb.checked = !!b.checked; cb.className = 'form-check-input todo-box';
            cb.addEventListener('change', () => { b.checked = cb.checked; row.classList.toggle('done', cb.checked); changed(b); });
            row.appendChild(cb);
            row.classList.toggle('done', !!b.checked);
        }
        if (b.type === 'hr') {
            const hr = document.createElement('hr'); hr.tabIndex = 0; hr.className = 'content';
            row.appendChild(hr);
        } else {
            const c = document.createElement(b.type === 'code' ? 'pre' : 'div');
            c.className = 'content';
            c.contentEditable = 'plaintext-only';
            c.spellcheck = b.type !== 'code';
            c.dataset.placeholder = b.type === 'p' ? 'Write, or start a line with # - 1. [] > ```' : TYPES[b.type];
            c.textContent = b.text;
            row.appendChild(c);
        }
        return row;
    }
    function render() {
        $list.replaceChildren(...state.blocks.map(rowFor));
        renumber();
        $title.value = state.title;
    }
    function renumber() {
        let n = 0;
        for (const row of $list.children) {
            n = row.dataset.type === 'ol' ? n + 1 : 0;
            if (n) row.dataset.n = n + '.'; else delete row.dataset.n;
        }
    }
    const rowOf = id => $list.querySelector(`.blk[data-id="${id}"]`);
    const blockOf = id => state.blocks.find(b => b.id === id);
    const indexOf = id => state.blocks.findIndex(b => b.id === id);

    function focusEnd(el, offset) {
        el = el?.querySelector('.content') ?? el;
        if (!el) return;
        el.focus();
        if (el.tagName === 'HR') return;
        const r = document.createRange(), sel = getSelection();
        const node = el.firstChild ?? el;
        const len = node.nodeType === 3 ? node.length : 0;
        r.setStart(node, Math.min(offset ?? len, len));
        r.collapse(true);
        sel.removeAllRanges(); sel.addRange(r);
    }
    function caret(el) {
        const sel = getSelection();
        if (!sel.rangeCount || !el.contains(sel.anchorNode)) return 0;
        const r = sel.getRangeAt(0).cloneRange();
        r.selectNodeContents(el); r.setEnd(sel.anchorNode, sel.anchorOffset);
        return r.toString().length;
    }

    // ---------- local edits -> ops
    const pending = new Map();
    function changed(b, after) {
        saveLocal();
        // text edits are batched per block for 120 ms; structural ops go out immediately
        clearTimeout(pending.get(b.id));
        pending.set(b.id, setTimeout(() => send({ kind: 'upsert', block: { ...b }, after }), after === undefined ? 120 : 0));
    }
    function insertAfter(ref, block) {
        const i = ref ? indexOf(ref.id) + 1 : 0;
        state.blocks.splice(i, 0, block);
        const row = rowFor(block);
        ref ? rowOf(ref.id).after(row) : $list.prepend(row);
        renumber();
        changed(block, ref ? ref.id : null);
        return row;
    }
    function remove(b) {
        state.blocks.splice(indexOf(b.id), 1);
        rowOf(b.id)?.remove();
        renumber(); saveLocal();
        send({ kind: 'delete', id: b.id });
    }
    function retype(b, type) {
        b.type = type;
        const old = rowOf(b.id), row = rowFor(b);
        old.replaceWith(row);
        renumber();
        changed(b);
        return row;
    }

    $list.addEventListener('input', e => {
        const row = e.target.closest('.blk'); if (!row) return;
        const b = blockOf(row.dataset.id);
        b.text = e.target.textContent;
        if (b.type === 'p') {
            for (const [re, type] of SHORTCUTS) {
                if (re.test(b.text)) {
                    b.text = '';
                    const nr = retype(b, type);
                    if (type === 'hr') { focusEnd(insertAfter(b, { id: uid(), type: 'p', text: '' })); }
                    else focusEnd(nr, 0);
                    return;
                }
            }
        }
        changed(b);
    });

    $list.addEventListener('keydown', e => {
        const row = e.target.closest('.blk'); if (!row) return;
        const b = blockOf(row.dataset.id);
        const el = row.querySelector('.content');
        if (e.key === 'Enter' && !e.shiftKey && b.type !== 'code') {
            e.preventDefault();
            const pos = caret(el), text = b.text;
            // an empty list item ends the list
            if (!text && ['ul', 'ol', 'todo', 'quote'].includes(b.type)) { focusEnd(retype(b, 'p'), 0); return; }
            b.text = text.slice(0, pos); el.textContent = b.text; changed(b);
            const nextType = ['ul', 'ol', 'todo'].includes(b.type) ? b.type : 'p';
            focusEnd(insertAfter(b, { id: uid(), type: nextType, text: text.slice(pos) }), 0);
        } else if (e.key === 'Backspace' && caret(el) === 0 && getSelection().isCollapsed) {
            if (b.type !== 'p') { e.preventDefault(); focusEnd(retype(b, 'p'), 0); return; }
            const i = indexOf(b.id);
            if (i === 0) return;
            e.preventDefault();
            const prev = state.blocks[i - 1];
            if (prev.type === 'hr') { remove(prev); return; }
            const at = prev.text.length;
            prev.text += b.text;
            rowOf(prev.id).querySelector('.content').textContent = prev.text;
            changed(prev); remove(b);
            focusEnd(rowOf(prev.id), at);
        } else if ((e.key === 'ArrowUp' || e.key === 'ArrowDown') && !e.shiftKey) {
            const target = e.key === 'ArrowUp' ? row.previousElementSibling : row.nextElementSibling;
            const atEdge = e.key === 'ArrowUp' ? caret(el) === 0 : caret(el) === (b.text?.length ?? 0);
            if (target && atEdge) { e.preventDefault(); focusEnd(target); }
        } else if (e.key === 'Tab' && b.type === 'code') {
            e.preventDefault(); document.execCommand('insertText', false, '    ');
        }
    });

    // pasting several lines of markdown creates several blocks
    $list.addEventListener('paste', e => {
        const text = e.clipboardData.getData('text/plain');
        const row = e.target.closest('.blk');
        if (!row || !text.includes('\n') || blockOf(row.dataset.id).type === 'code') return;
        e.preventDefault();
        let ref = blockOf(row.dataset.id), last;
        for (const nb of parseMarkdown(text)) { last = insertAfter(ref, nb); ref = nb; }
        focusEnd(last);
    });

    $list.addEventListener('focusin', e => { const row = e.target.closest('.blk'); if (row) presence(row.dataset.id); });
    $list.addEventListener('focusout', () => presence(null));

    $title.addEventListener('input', () => {
        state.title = $title.value; saveLocal();
        clearTimeout(pending.get('title'));
        pending.set('title', setTimeout(() => send({ kind: 'title', title: state.title }), 300));
    });

    // ---------- F3: drag and drop reordering
    Sortable.create($list, {
        handle: '.handle', animation: 180, ghostClass: 'blk-ghost', chosenClass: 'blk-chosen',
        onEnd: ev => {
            if (ev.oldIndex === ev.newIndex) return;
            const [moved] = state.blocks.splice(ev.oldIndex, 1);
            state.blocks.splice(ev.newIndex, 0, moved);
            renumber(); saveLocal();
            send({ kind: 'move', id: moved.id, after: ev.newIndex ? state.blocks[ev.newIndex - 1].id : null });
        },
    });

    // ---------- markdown in / out
    function parseMarkdown(md) {
        const out = [];
        const lines = md.replace(/\r/g, '').split('\n');
        for (let i = 0; i < lines.length; i++) {
            const l = lines[i];
            if (l.startsWith('```')) {
                const body = [];
                while (++i < lines.length && !lines[i].startsWith('```')) body.push(lines[i]);
                out.push({ id: uid(), type: 'code', text: body.join('\n') }); continue;
            }
            const m = [[/^### (.*)/, 'h3'], [/^## (.*)/, 'h2'], [/^# (.*)/, 'h1'], [/^- \[( |x)\] (.*)/i, 'todo'],
                       [/^[-*] (.*)/, 'ul'], [/^\d+[.)] (.*)/, 'ol'], [/^> ?(.*)/, 'quote'], [/^(---|\*\*\*)$/, 'hr']]
                .map(([re, t]) => [l.match(re), t]).find(([x]) => x);
            if (m) {
                const [x, t] = m;
                if (t === 'todo') out.push({ id: uid(), type: t, text: x[2], checked: x[1].toLowerCase() === 'x' });
                else out.push({ id: uid(), type: t, text: t === 'hr' ? '' : x[1] });
            } else if (l.trim()) out.push({ id: uid(), type: 'p', text: l });
        }
        return out;
    }
    function toMarkdown() {
        let n = 0;
        const line = b => {
            n = b.type === 'ol' ? n + 1 : 0;
            switch (b.type) {
                case 'h1': return '# ' + b.text;
                case 'h2': return '## ' + b.text;
                case 'h3': return '### ' + b.text;
                case 'ul': return '- ' + b.text;
                case 'ol': return `${n}. ` + b.text;
                case 'todo': return (b.checked ? '- [x] ' : '- [ ] ') + b.text;
                case 'quote': return '> ' + b.text;
                case 'code': return '```\n' + b.text + '\n```';
                case 'hr': return '---';
                default: return b.text;
            }
        };
        return `# ${state.title}\n\n` + state.blocks.map(line).join('\n') + '\n';
    }
    document.getElementById('export-md')?.addEventListener('click', () => {
        const a = document.createElement('a');
        a.href = URL.createObjectURL(new Blob([toMarkdown()], { type: 'text/markdown' }));
        a.download = (state.title || 'notes').replace(/[^\w-]+/g, '-') + '.md';
        a.click(); URL.revokeObjectURL(a.href);
    });
    document.getElementById('import-md')?.addEventListener('change', async e => {
        const f = e.target.files[0]; if (!f) return;
        const blocks = parseMarkdown(await f.text());
        let ref = state.blocks.at(-1) ?? null;
        for (const nb of blocks) { insertAfter(ref, nb); ref = nb; }
        e.target.value = '';
    });

    // ---------- C13/C14: live sync
    function send(op) {
        if (!hub || !connected) return;
        hub.invoke('Op', cfg.docId, op).catch(err => console.warn('op failed', err));
    }
    let presenceTimer;
    function presence(id) {
        if (!hub || !connected) return;
        clearTimeout(presenceTimer);
        presenceTimer = setTimeout(() => hub.invoke('Focus', cfg.docId, id).catch(() => {}), 80);
    }

    function flash(row, peer, text) {
        if (!row || !peer) return;
        row.style.setProperty('--peer', peer.color);
        row.dataset.who = `${peer.name} ${text}`;
        row.classList.remove('remote'); void row.offsetWidth; row.classList.add('remote');
        clearTimeout(row._t); row._t = setTimeout(() => row.classList.remove('remote'), 1800);
    }
    // FLIP: remember positions, change the DOM, animate every row from where it was to where it is
    function animateReorder(mutate) {
        const before = new Map([...$list.children].map(r => [r.dataset.id, r.getBoundingClientRect().top]));
        mutate();
        for (const r of $list.children) {
            const top = before.get(r.dataset.id);
            if (top === undefined) continue;
            const dy = top - r.getBoundingClientRect().top;
            if (!dy) continue;
            r.animate([{ transform: `translateY(${dy}px)` }, { transform: 'none' }], { duration: 420, easing: 'cubic-bezier(.2,.8,.2,1)' });
        }
    }

    function applyRemote(op, by) {
        if (op.kind === 'title') { state.title = op.title; if (document.activeElement !== $title) $title.value = op.title; flash($title.parentElement, by, 'renamed'); saveLocal(); return; }
        if (op.kind === 'delete') {
            const row = rowOf(op.id);
            if (row) { row.classList.add('leaving'); setTimeout(() => animateReorder(() => row.remove()), 250); }
            state.blocks = state.blocks.filter(b => b.id !== op.id);
            setTimeout(renumber, 300); saveLocal(); return;
        }
        if (op.kind === 'move') {
            const i = indexOf(op.id); if (i < 0) return;
            const [b] = state.blocks.splice(i, 1);
            const to = op.after ? indexOf(op.after) + 1 : 0;
            state.blocks.splice(to, 0, b);
            animateReorder(() => { const row = rowOf(op.id); to ? rowOf(op.after).after(row) : $list.prepend(row); });
            renumber(); flash(rowOf(op.id), by, 'moved this'); saveLocal(); return;
        }
        if (op.kind === 'upsert') {
            const nb = op.block, existing = blockOf(nb.id);
            if (existing) {
                const row = rowOf(nb.id), el = row.querySelector('.content');
                const typeChanged = existing.type !== nb.type || !!existing.checked !== !!nb.checked;
                Object.assign(existing, nb);
                if (typeChanged) row.replaceWith(rowFor(existing));
                else if (el && el.textContent !== nb.text) {
                    const mine = document.activeElement === el, pos = mine ? caret(el) : 0;
                    el.textContent = nb.text;
                    if (mine) focusEnd(el, pos);
                }
                renumber(); flash(rowOf(nb.id), by, 'is editing');
            } else {
                const to = op.after ? indexOf(op.after) + 1 : 0;
                state.blocks.splice(to, 0, nb);
                const row = rowFor(nb);
                animateReorder(() => op.after && rowOf(op.after) ? rowOf(op.after).after(row) : $list.prepend(row));
                row.animate([{ opacity: 0, transform: 'scaleY(.6)' }, { opacity: 1, transform: 'none' }], { duration: 300 });
                renumber(); flash(row, by, 'added this');
            }
            saveLocal();
        }
    }

    function showPeers(list) {
        const others = list.filter(p => p.connectionId !== me?.connectionId);
        $peers.innerHTML = others.map(p => `<span class="peer" style="--peer:${p.color}" title="${p.name}">${p.name[0] ?? '?'}</span>`).join('')
            + (others.length ? `<span class="muted small ms-1">${others.length} other${others.length > 1 ? 's' : ''} here</span>` : '<span class="muted small">only you</span>');
        // drop cursors of people who left
        for (const r of $list.querySelectorAll('.blk[data-cursor]'))
            if (!others.some(p => p.connectionId === r.dataset.cursor)) { r.removeAttribute('data-cursor'); r.style.removeProperty('--cursor'); }
    }
    function showCursor(peer, blockId) {
        for (const r of $list.querySelectorAll(`.blk[data-cursor="${peer.connectionId}"]`)) { r.removeAttribute('data-cursor'); r.removeAttribute('data-cursor-name'); }
        const row = blockId && rowOf(blockId);
        if (row) { row.dataset.cursor = peer.connectionId; row.dataset.cursorName = peer.name; row.style.setProperty('--cursor', peer.color); }
    }

    function paintStatus() {
        $status.textContent = !cfg.docId ? `Saved in this browser · ${$status.dataset.local ?? ''}`
            : connected ? `Live · synced · local copy ${$status.dataset.local ?? ''}` : `Offline · changes kept in this browser`;
        $status.className = 'tag ' + (connected || !cfg.docId ? 'soon' : 'live');
    }

    async function startLive() {
        hub = new signalR.HubConnectionBuilder().withUrl('/hubs/docs').withAutomaticReconnect([0, 1000, 3000, 5000, 10000]).build();
        hub.on('op', (op, by) => applyRemote(op, by));
        hub.on('peers', showPeers);
        hub.on('focus', showCursor);
        const join = async () => {
            const r = await hub.invoke('Join', cfg.docId);
            me = r.me;
            state = { title: r.title, blocks: r.blocks };
            render(); saveLocal(); showPeers(r.peers);
            connected = true; paintStatus();
        };
        hub.onreconnecting(() => { connected = false; paintStatus(); });
        hub.onreconnected(join);
        hub.onclose(() => { connected = false; paintStatus(); });
        try { await hub.start(); await join(); }
        catch (e) { console.warn(e); connected = false; paintStatus(); }
    }

    // ---------- boot
    const local = loadLocal();
    if (cfg.docId) {
        state = local ?? { title: cfg.title, blocks: cfg.blocks };   // show instantly, then the server copy wins
        render(); paintStatus(); startLive();
    } else {
        state = local ?? { title: 'My notes', blocks: parseMarkdown(`# Scouting notes\nThis page lives only in your browser. Close it, come back: it's still here.\n## Try\n- type # at the start of a line for a heading\n- drag a block by its ⠿ handle\n- [ ] open this page in two tabs and edit one of them`) };
        render(); saveLocal();
        // two tabs of the same browser stay in sync through the storage event
        addEventListener('storage', e => {
            if (e.key !== KEY || !e.newValue) return;
            const next = JSON.parse(e.newValue);
            const focusedId = document.activeElement?.closest('.blk')?.dataset.id;
            state = next; render();
            if (focusedId) focusEnd(rowOf(focusedId));
        });
    }
})();
