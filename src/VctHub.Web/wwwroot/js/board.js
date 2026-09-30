// C18 shared tactical board + C15 low-level browser APIs (Canvas, File API, WebRTC, Web Audio).
(async function () {
    const cfg = window.BOARD;
    const $canvas = document.getElementById('canvas');
    const ctx = $canvas.getContext('2d');
    const $img = document.getElementById('map-img');
    const $square = document.getElementById('square');
    const $cursors = document.getElementById('cursors');
    const uid = () => Math.random().toString(36).slice(2, 11);

    // ---------- data from valorant-api.com (maps + agent icons)
    const [maps, agents] = await Promise.all([
        fetch('https://valorant-api.com/v1/maps').then(r => r.json()).then(j => j.data.filter(m => m.displayIcon && m.tacticalDescription)),
        fetch('https://valorant-api.com/v1/agents?isPlayableCharacter=true').then(r => r.json()).then(j => j.data),
    ]);
    const agentImg = {};
    for (const a of agents) { const i = new Image(); i.crossOrigin = 'anonymous'; i.src = a.displayIcon; i.onload = () => dirty = true; agentImg[a.uuid] = i; }

    const $map = document.getElementById('map-select');
    $map.innerHTML = maps.sort((a, b) => a.displayName.localeCompare(b.displayName)).map(m => `<option value="${m.uuid}">${m.displayName}</option>`).join('');
    document.getElementById('agents').innerHTML = agents.sort((a, b) => a.displayName.localeCompare(b.displayName))
        .map(a => `<img src="${a.displayIcon}" data-agent="${a.uuid}" title="${a.displayName}" alt="${a.displayName}" draggable="true">`).join('');

    // ---------- state
    let items = [];              // committed items, same list on every client
    const drafts = new Map();    // connectionId -> item being drawn by someone else
    let me = null, dirty = true;
    let local = null, dragging = null, lastDraft = 0; // my stroke in progress / marker being dragged
    const tool = { name: 'pen', color: '#ff4655', agent: null };

    function setMapBackground(id) {
        $img.src = id.startsWith('data:') ? id : maps.find(m => m.uuid === id)?.displayIcon ?? '';
        if (!id.startsWith('data:')) $map.value = id;
    }

    // ---------- drawing (coordinates are 0..1 so every screen size lines up)
    function fit() {
        const r = $canvas.getBoundingClientRect(), dpr = devicePixelRatio || 1;
        $canvas.width = r.width * dpr; $canvas.height = r.height * dpr;
        ctx.setTransform(dpr * r.width, 0, 0, dpr * r.height, 0, 0);
        dirty = true;
    }
    new ResizeObserver(fit).observe($canvas);

    function drawItem(it, alpha = 1) {
        ctx.globalAlpha = alpha;
        const lw = (it.width ?? 4) / $canvas.getBoundingClientRect().width;
        ctx.lineWidth = lw; ctx.lineCap = 'round'; ctx.lineJoin = 'round';
        ctx.strokeStyle = ctx.fillStyle = it.color ?? '#ff4655';
        if (it.kind === 'stroke' && it.points.length) {
            ctx.beginPath(); ctx.moveTo(...it.points[0]);
            for (let i = 1; i < it.points.length - 1; i++) {
                const [x1, y1] = it.points[i], [x2, y2] = it.points[i + 1];
                ctx.quadraticCurveTo(x1, y1, (x1 + x2) / 2, (y1 + y2) / 2);
            }
            ctx.lineTo(...it.points.at(-1)); ctx.stroke();
        } else if (it.kind === 'arrow') {
            const [x1, y1] = it.from, [x2, y2] = it.to, a = Math.atan2(y2 - y1, x2 - x1), h = lw * 5;
            ctx.beginPath(); ctx.moveTo(x1, y1); ctx.lineTo(x2, y2); ctx.stroke();
            ctx.beginPath(); ctx.moveTo(x2, y2);
            ctx.lineTo(x2 - h * Math.cos(a - .45), y2 - h * Math.sin(a - .45));
            ctx.lineTo(x2 - h * Math.cos(a + .45), y2 - h * Math.sin(a + .45));
            ctx.closePath(); ctx.fill();
        } else if (it.kind === 'agent') {
            const s = 0.045, img = agentImg[it.agent];
            ctx.beginPath(); ctx.arc(it.x, it.y, s / 2 + 0.004, 0, Math.PI * 2);
            ctx.fillStyle = it.side === 'def' ? '#3fd1b4' : '#ff4655'; ctx.fill();
            ctx.save(); ctx.beginPath(); ctx.arc(it.x, it.y, s / 2, 0, Math.PI * 2); ctx.clip();
            ctx.fillStyle = '#0f1923'; ctx.fillRect(it.x - s / 2, it.y - s / 2, s, s);
            if (img?.complete) ctx.drawImage(img, it.x - s / 2, it.y - s / 2, s, s);
            ctx.restore();
        }
        ctx.globalAlpha = 1;
    }
    (function loop() {
        if (dirty) {
            dirty = false;
            ctx.clearRect(0, 0, 1, 1);
            items.forEach(it => drawItem(it));
            drafts.forEach(d => drawItem(d, .65));
            if (local) drawItem(local, .9);
        }
        requestAnimationFrame(loop);
    })();

    // ---------- pointer input
    const pos = e => { const r = $canvas.getBoundingClientRect(); return [(e.clientX - r.left) / r.width, (e.clientY - r.top) / r.height]; };
    const hit = ([x, y]) => [...items].reverse().find(it =>
        it.kind === 'agent' ? Math.hypot(it.x - x, it.y - y) < .03
        : it.kind === 'arrow' ? distToSeg([x, y], it.from, it.to) < .012
        : it.points?.some(p => Math.hypot(p[0] - x, p[1] - y) < .012));
    function distToSeg(p, a, b) {
        const l2 = (b[0] - a[0]) ** 2 + (b[1] - a[1]) ** 2;
        const t = l2 ? Math.max(0, Math.min(1, ((p[0] - a[0]) * (b[0] - a[0]) + (p[1] - a[1]) * (b[1] - a[1])) / l2)) : 0;
        return Math.hypot(p[0] - (a[0] + t * (b[0] - a[0])), p[1] - (a[1] + t * (b[1] - a[1])));
    }

    $canvas.addEventListener('pointerdown', e => {
        $canvas.setPointerCapture(e.pointerId);
        const p = pos(e);
        if (tool.agent) {
            const it = { id: uid(), kind: 'agent', agent: tool.agent, side: document.querySelector('[name=side]:checked').value, x: p[0], y: p[1], by: me?.id };
            commit(it); return;
        }
        if (tool.name === 'erase') { const it = hit(p); if (it) removeItem(it.id, true); return; }
        if (tool.name === 'move') { const it = hit(p); if (it?.kind === 'agent') dragging = it; return; }
        local = tool.name === 'arrow'
            ? { id: uid(), kind: 'arrow', from: p, to: p, color: tool.color, width: 5, by: me?.id }
            : { id: uid(), kind: 'stroke', points: [p], color: tool.color, width: 4, by: me?.id };
    });
    $canvas.addEventListener('pointermove', e => {
        const p = pos(e);
        sendCursor(p);
        if (tool.name === 'erase' && e.buttons) { const it = hit(p); if (it) removeItem(it.id, true); }
        if (dragging) { dragging.x = p[0]; dragging.y = p[1]; dirty = true; throttleDraft(dragging); return; }
        if (!local) return;
        if (local.kind === 'arrow') local.to = p;
        else if (local.points.length < 1500) local.points.push(p.map(v => Math.round(v * 10000) / 10000));
        dirty = true; throttleDraft(local);
    });
    $canvas.addEventListener('pointerup', () => {
        if (dragging) { send('Update', dragging); dragging = null; return; }
        if (!local) return;
        if (local.kind === 'stroke' && local.points.length < 2) local.points.push(local.points[0]);
        commit(local); local = null;
    });
    function throttleDraft(d) { const now = performance.now(); if (now - lastDraft > 40) { lastDraft = now; send('Draft', d); } }

    // ---------- toolbar
    document.getElementById('tools').addEventListener('click', e => {
        const b = e.target.closest('button'); if (!b) return;
        tool.name = b.dataset.tool; tool.agent = null;
        document.querySelectorAll('#tools button, #agents img').forEach(x => x.classList.toggle('on', x === b));
    });
    document.getElementById('colors').addEventListener('click', e => {
        const b = e.target.closest('button'); if (!b) return;
        tool.color = b.dataset.color;
        document.querySelectorAll('#colors button').forEach(x => x.classList.toggle('on', x === b));
    });
    document.getElementById('agents').addEventListener('click', e => {
        const img = e.target.closest('img'); if (!img) return;
        tool.agent = img.dataset.agent;
        document.querySelectorAll('#tools button, #agents img').forEach(x => x.classList.toggle('on', x === img));
    });
    // agents can also be dragged from the palette straight onto the map
    document.getElementById('agents').addEventListener('dragstart', e => e.dataTransfer.setData('text/agent', e.target.dataset.agent));
    addEventListener('keydown', e => {
        if (e.target.matches('input, select')) return;
        if ((e.ctrlKey || e.metaKey) && e.key === 'z') { e.preventDefault(); undo(); }
        const t = { p: 'pen', a: 'arrow', m: 'move', e: 'erase' }[e.key];
        if (t) document.querySelector(`[data-tool=${t}]`).click();
    });
    function undo() {
        const mine = [...items].reverse().find(it => it.by === me?.id);
        if (mine) removeItem(mine.id, true);
    }
    document.getElementById('undo').onclick = undo;
    document.getElementById('clear').onclick = () => { if (confirm('Clear the board for everyone?')) { items = []; dirty = true; send('Clear'); } };
    $map.onchange = () => { items = []; setMapBackground($map.value); dirty = true; send('SetMap', $map.value); };
    document.getElementById('copy-link').onclick = async e => { await navigator.clipboard.writeText(location.href); e.target.textContent = 'Link copied'; };
    document.getElementById('png').onclick = () => {
        const out = document.createElement('canvas'); out.width = out.height = 1024;
        const o = out.getContext('2d');
        o.fillStyle = '#0f1923'; o.fillRect(0, 0, 1024, 1024);
        try { o.drawImage($img, 0, 0, 1024, 1024); } catch { }
        o.drawImage($canvas, 0, 0, 1024, 1024);
        const a = document.createElement('a'); a.download = `strat-${cfg.room}.png`;
        try { a.href = out.toDataURL('image/png'); a.click(); } catch { alert('This background image does not allow export.'); }
    };

    // ---------- File API: drop an image to use it as the map (downscaled so it fits in one message)
    $square.addEventListener('dragover', e => { e.preventDefault(); if ([...e.dataTransfer.types].includes('Files')) $square.classList.add('dragover'); });
    $square.addEventListener('dragleave', () => $square.classList.remove('dragover'));
    $square.addEventListener('drop', async e => {
        e.preventDefault(); $square.classList.remove('dragover');
        const agent = e.dataTransfer.getData('text/agent');
        if (agent) { const p = pos(e); commit({ id: uid(), kind: 'agent', agent, side: document.querySelector('[name=side]:checked').value, x: p[0], y: p[1], by: me?.id }); return; }
        const file = [...e.dataTransfer.files].find(f => f.type.startsWith('image/'));
        if (!file) return;
        const url = await new Promise(res => { const fr = new FileReader(); fr.onload = () => res(fr.result); fr.readAsDataURL(file); });
        const img = await new Promise(res => { const i = new Image(); i.onload = () => res(i); i.src = url; });
        const c = document.createElement('canvas'), size = 900, k = size / Math.max(img.width, img.height);
        c.width = c.height = size;
        const cx = c.getContext('2d'); cx.fillStyle = '#0f1923'; cx.fillRect(0, 0, size, size);
        cx.drawImage(img, (size - img.width * k) / 2, (size - img.height * k) / 2, img.width * k, img.height * k);
        const data = c.toDataURL('image/jpeg', 0.72);
        items = []; setMapBackground(data); dirty = true; send('SetMap', data);
    });

    // ---------- sync
    const hub = new signalR.HubConnectionBuilder().withUrl('/hubs/board').withAutomaticReconnect().build();
    function send(method, ...args) { if (hub.state === 'Connected') hub.invoke(method, ...args).catch(err => console.warn(method, err)); }
    function commit(it) { items.push(it); dirty = true; send('Add', it); }
    function removeItem(id, broadcast) { items = items.filter(x => x.id !== id); dirty = true; if (broadcast) send('Remove', id); }

    hub.on('change', (kind, payload, from) => {
        drafts.delete(from);
        if (kind === 'add') items.push(payload);
        else if (kind === 'update') { const i = items.findIndex(x => x.id === payload.id); if (i >= 0) items[i] = payload; }
        else if (kind === 'remove') items = items.filter(x => x.id !== payload);
        else if (kind === 'clear') items = [];
        else if (kind === 'map') { items = []; setMapBackground(payload); }
        dirty = true;
    });
    hub.on('draft', (from, d) => { drafts.set(from, d); if (d.kind === 'agent') { const it = items.find(x => x.id === d.id); if (it) { it.x = d.x; it.y = d.y; drafts.delete(from); } } dirty = true; });
    hub.on('cursor', (from, x, y) => showCursor(from, x, y));
    hub.on('left', from => { document.getElementById('c-' + from)?.remove(); drafts.delete(from); closePeer(from); dirty = true; });
    hub.on('peers', renderPeers);
    hub.on('signal', onSignal);

    let peersList = [];
    function renderPeers(list) {
        peersList = list;
        document.getElementById('peers').innerHTML = list.map(p =>
            `<div class="peer-row" style="--c:${p.color}"><span class="peer-dot"></span>${p.name}${p.id === me?.id ? ' (you)' : ''}${p.voice ? ' <span id="spk-' + p.id + '">🎙</span>' : ''}</div>`).join('');
        document.getElementById('voice-peers').textContent = list.filter(p => p.voice).length ? `${list.filter(p => p.voice).length} in voice` : 'nobody in voice';
        if (voiceOn) list.filter(p => p.voice && p.id !== me.id && !pcs.has(p.id) && p.id < me.id).forEach(p => call(p.id));
    }
    function showCursor(from, x, y) {
        const p = peersList.find(q => q.id === from); if (!p) return;
        let el = document.getElementById('c-' + from);
        if (!el) {
            el = document.createElement('div'); el.className = 'cursor'; el.id = 'c-' + from;
            el.innerHTML = `<svg width="16" height="16" viewBox="0 0 16 16"><path d="M1 1l5 14 2-6 6-2z" fill="${p.color}" stroke="#0f1923"/></svg><span>${p.name}</span>`;
            el.style.setProperty('--c', p.color); $cursors.appendChild(el);
        }
        el.style.left = x * 100 + '%'; el.style.top = y * 100 + '%';
    }
    let lastCursor = 0;
    function sendCursor(p) { const now = performance.now(); if (now - lastCursor > 50) { lastCursor = now; send('Cursor', p[0], p[1]); } }

    let name = cfg.signedIn ? null : (localStorage.getItem('vct.board.name') || prompt('Your name on the board', 'Coach') || 'Guest');
    if (name) try { localStorage.setItem('vct.board.name', name); } catch { }
    await hub.start();
    const joined = await hub.invoke('Join', cfg.room, name);
    me = joined.me; items = joined.items; setMapBackground(joined.map); renderPeers(joined.peers); dirty = true;
    hub.onreconnected(async () => { const j = await hub.invoke('Join', cfg.room, name); me = j.me; items = j.items; setMapBackground(j.map); renderPeers(j.peers); dirty = true; });

    // ---------- C15: WebRTC voice (mesh; the hub only relays offers/answers/ICE)
    const pcs = new Map();
    let voiceOn = false, micStream = null;
    const ICE = { iceServers: [{ urls: 'stun:stun.l.google.com:19302' }, { urls: 'stun:stun1.l.google.com:19302' }] };

    function makePeer(id) {
        const pc = new RTCPeerConnection(ICE);
        micStream.getTracks().forEach(t => pc.addTrack(t, micStream));
        pc.onicecandidate = e => e.candidate && send('Signal', id, { candidate: e.candidate });
        pc.ontrack = e => {
            let audio = document.getElementById('a-' + id);
            if (!audio) { audio = document.createElement('audio'); audio.id = 'a-' + id; audio.autoplay = true; document.getElementById('audio-sink').appendChild(audio); }
            audio.srcObject = e.streams[0];
            watchLevel(e.streams[0], id);
        };
        pc.onconnectionstatechange = () => { if (['failed', 'closed'].includes(pc.connectionState)) closePeer(id); };
        pcs.set(id, pc);
        return pc;
    }
    async function call(id) {
        const pc = makePeer(id);
        await pc.setLocalDescription(await pc.createOffer());
        send('Signal', id, { sdp: pc.localDescription });
    }
    async function onSignal(from, msg) {
        if (!voiceOn) return;
        let pc = pcs.get(from);
        if (msg.sdp) {
            if (msg.sdp.type === 'offer') {
                pc ??= makePeer(from);
                await pc.setRemoteDescription(msg.sdp);
                await pc.setLocalDescription(await pc.createAnswer());
                send('Signal', from, { sdp: pc.localDescription });
            } else if (pc) await pc.setRemoteDescription(msg.sdp);
        } else if (msg.candidate && pc) {
            try { await pc.addIceCandidate(msg.candidate); } catch { }
        }
    }
    function closePeer(id) { pcs.get(id)?.close(); pcs.delete(id); document.getElementById('a-' + id)?.remove(); }

    // Web Audio: light up the mic icon of whoever is talking
    const audioCtx = window.AudioContext ? new AudioContext() : null;
    function watchLevel(stream, id) {
        if (!audioCtx) return;
        const an = audioCtx.createAnalyser(); an.fftSize = 256;
        audioCtx.createMediaStreamSource(stream).connect(an);
        const buf = new Uint8Array(an.frequencyBinCount);
        (function tick() {
            if (!pcs.has(id) && id !== me.id) return;
            an.getByteFrequencyData(buf);
            const level = buf.reduce((a, b) => a + b, 0) / buf.length;
            document.getElementById('spk-' + id)?.classList.toggle('speaking', level > 18);
            requestAnimationFrame(tick);
        })();
    }

    document.getElementById('voice').onclick = async e => {
        if (!voiceOn) {
            try { micStream = await navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: true, noiseSuppression: true } }); }
            catch { alert('Microphone permission is needed for voice.'); return; }
            voiceOn = true; audioCtx?.resume(); watchLevel(micStream, me.id);
            e.target.textContent = '🔇 Leave voice';
            send('Voice', true);
        } else {
            voiceOn = false;
            [...pcs.keys()].forEach(closePeer);
            micStream.getTracks().forEach(t => t.stop()); micStream = null;
            e.target.textContent = '🎙 Join voice';
            send('Voice', false);
        }
    };
})();
