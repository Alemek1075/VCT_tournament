// F6: aim trainer on a 2D canvas. Gridshot = 3 targets at once, Flick = one target that shrinks.
(function () {
    const canvas = document.getElementById('game'), ctx = canvas.getContext('2d');
    const $ = id => document.getElementById(id);
    const ROUND = 30;
    let mode = 'gridshot', W = 0, H = 0, dpr = 1;
    let game = null, mouse = { x: -100, y: -100 };

    function fit() {
        const r = canvas.getBoundingClientRect(); dpr = devicePixelRatio || 1;
        W = r.width; H = r.height; canvas.width = W * dpr; canvas.height = H * dpr;
        ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    }
    new ResizeObserver(fit).observe(canvas);

    // --- sound: tiny synth blips, no audio files
    const ac = window.AudioContext ? new AudioContext() : null;
    function blip(freq, dur = .06, type = 'square', vol = .05) {
        if (!ac) return;
        const o = ac.createOscillator(), g = ac.createGain();
        o.type = type; o.frequency.value = freq; g.gain.value = vol;
        g.gain.exponentialRampToValueAtTime(.0001, ac.currentTime + dur);
        o.connect(g).connect(ac.destination); o.start(); o.stop(ac.currentTime + dur);
    }

    // --- targets
    function spawn() {
        const r = mode === 'flick' ? Math.max(W, H) * .035 : Math.max(W, H) * .028;
        const m = r * 2 + 40;
        let t, tries = 0;
        do {
            t = { x: m + Math.random() * (W - 2 * m), y: m + 30 + Math.random() * (H - 2 * m - 30), r, born: performance.now(), life: mode === 'flick' ? 1400 : Infinity };
        } while (game.targets.some(o => Math.hypot(o.x - t.x, o.y - t.y) < o.r + t.r + 20) && ++tries < 30);
        game.targets.push(t);
    }

    function start() {
        ac?.resume();
        game = { t0: performance.now(), score: 0, hits: 0, misses: 0, combo: 0, reactions: [], targets: [], particles: [], floaters: [], over: false };
        for (let i = 0; i < (mode === 'gridshot' ? 3 : 1); i++) spawn();
        $('overlay').hidden = true;
        requestAnimationFrame(frame);
    }

    function finish() {
        game.over = true;
        const acc = game.hits / Math.max(1, game.hits + game.misses);
        const avg = game.reactions.length ? Math.round(game.reactions.reduce((a, b) => a + b, 0) / game.reactions.length) : 0;
        $('ov-title').textContent = `${game.score} points`;
        $('ov-text').textContent = `${game.hits} hits · ${Math.round(acc * 100)}% accuracy · ${avg} ms average reaction`;
        $('start').textContent = 'Again';
        $('overlay').hidden = false;
        const bestKey = 'vct.aim.best.' + mode;
        const best = +(localStorage.getItem(bestKey) || 0);
        if (game.score > best) try { localStorage.setItem(bestKey, game.score); } catch { }
        showBest();
        if (window.AIM.signedIn && game.hits > 0) {
            fetch('/api/aim/scores', { method: 'POST', headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ mode, score: game.score, hits: game.hits, misses: game.misses, avgReactionMs: Math.max(50, avg) }) })
                .then(r => r.ok ? r.json() : null)
                .then(r => { $('ov-save').textContent = r ? `Saved · #${r.rank} on the leaderboard` : 'Could not save the score'; loadBoard(); });
            window.track?.('aim_round', { mode, score: game.score });
        }
    }

    canvas.addEventListener('pointermove', e => { const r = canvas.getBoundingClientRect(); mouse = { x: e.clientX - r.left, y: e.clientY - r.top }; });
    canvas.addEventListener('pointerdown', e => {
        if (!game || game.over) return;
        const r = canvas.getBoundingClientRect(), x = e.clientX - r.left, y = e.clientY - r.top;
        const i = game.targets.findIndex(t => Math.hypot(t.x - x, t.y - y) <= t.r);
        if (i < 0) { game.misses++; game.combo = 0; blip(140, .08, 'sawtooth', .03); return; }
        const t = game.targets[i], d = Math.hypot(t.x - x, t.y - y) / t.r;
        const center = d < .35;
        game.combo++;
        const pts = Math.round((center ? 100 : 60) * (1 + Math.min(game.combo, 20) * .05));
        game.score += pts; game.hits++;
        game.reactions.push(Math.round(performance.now() - t.born));
        game.targets.splice(i, 1); spawn();
        for (let k = 0; k < 14; k++) {
            const a = Math.random() * Math.PI * 2, v = 1.5 + Math.random() * 3.5;
            game.particles.push({ x: t.x, y: t.y, vx: Math.cos(a) * v, vy: Math.sin(a) * v, life: 1 });
        }
        game.floaters.push({ x: t.x, y: t.y - t.r, text: (center ? '★ ' : '+') + pts, life: 1 });
        blip(center ? 1320 : 880);
    });
    addEventListener('keydown', e => { if (e.key === 'Escape' && game && !game.over) finish(); });

    function frame(now) {
        if (!game || game.over) return;
        const left = Math.max(0, ROUND - (now - game.t0) / 1000);
        // flick targets expire: counts as a miss
        for (const t of [...game.targets]) if (now - t.born > t.life) {
            game.targets.splice(game.targets.indexOf(t), 1); game.misses++; game.combo = 0; spawn();
        }

        ctx.clearRect(0, 0, W, H);
        // floor grid for depth
        ctx.strokeStyle = 'rgba(255,255,255,.04)'; ctx.lineWidth = 1;
        for (let x = 0; x < W; x += 40) { ctx.beginPath(); ctx.moveTo(x, 0); ctx.lineTo(x, H); ctx.stroke(); }
        for (let y = 0; y < H; y += 40) { ctx.beginPath(); ctx.moveTo(0, y); ctx.lineTo(W, y); ctx.stroke(); }

        for (const t of game.targets) {
            const age = (now - t.born) / 1000;
            const k = t.life === Infinity ? Math.min(1, age * 6) : Math.max(.25, 1 - (now - t.born) / t.life);
            const r = t.r * k;
            ctx.beginPath(); ctx.arc(t.x, t.y, r, 0, Math.PI * 2); ctx.fillStyle = '#ff4655'; ctx.fill();
            ctx.beginPath(); ctx.arc(t.x, t.y, r * .66, 0, Math.PI * 2); ctx.fillStyle = '#0f0b0b'; ctx.fill();
            ctx.beginPath(); ctx.arc(t.x, t.y, r * .35, 0, Math.PI * 2); ctx.fillStyle = '#f2eee7'; ctx.fill();
            t.hitR = r;
        }
        for (const p of game.particles) {
            p.x += p.vx; p.y += p.vy; p.vy += .12; p.life -= .03;
            ctx.globalAlpha = Math.max(0, p.life); ctx.fillStyle = '#ff4655'; ctx.fillRect(p.x, p.y, 3, 3);
        }
        game.particles = game.particles.filter(p => p.life > 0);
        ctx.font = '700 18px "Barlow Condensed"'; ctx.textAlign = 'center';
        for (const f of game.floaters) {
            f.y -= .7; f.life -= .02; ctx.globalAlpha = Math.max(0, f.life); ctx.fillStyle = '#cfb473'; ctx.fillText(f.text, f.x, f.y);
        }
        game.floaters = game.floaters.filter(f => f.life > 0);
        ctx.globalAlpha = 1;

        // Valorant-style crosshair
        ctx.strokeStyle = '#60ddc0'; ctx.lineWidth = 2;
        const { x, y } = mouse;
        [[-10, 0, -4, 0], [4, 0, 10, 0], [0, -10, 0, -4], [0, 4, 0, 10]].forEach(([a, b, c, d]) => { ctx.beginPath(); ctx.moveTo(x + a, y + b); ctx.lineTo(x + c, y + d); ctx.stroke(); });

        $('hud-score').textContent = game.score + (game.combo > 2 ? ` ×${game.combo}` : '');
        $('hud-time').textContent = left.toFixed(1);
        $('hud-acc').textContent = Math.round(100 * game.hits / Math.max(1, game.hits + game.misses)) + '%';
        if (left <= 0) { finish(); return; }
        requestAnimationFrame(frame);
    }

    // --- leaderboard
    async function loadBoard() {
        $('lb-mode').textContent = mode;
        const rows = await fetch('/api/aim/leaderboard?mode=' + mode).then(r => r.json()).catch(() => []);
        $('lb').innerHTML = rows.length ? rows.map((r, i) => `<tr><td>${i + 1}</td><td>${r.player.replace(/</g, '&lt;')}</td><td class="text-end"><strong>${r.score}</strong></td><td class="text-end muted">${r.avgReactionMs}</td></tr>`).join('')
            : '<tr><td colspan="4" class="muted">No scores yet — be first.</td></tr>';
    }
    function showBest() { $('best').textContent = `Your best in this browser: ${localStorage.getItem('vct.aim.best.' + mode) || 0}`; }

    document.querySelectorAll('[data-mode]').forEach(b => b.addEventListener('click', () => {
        mode = b.dataset.mode;
        document.querySelectorAll('[data-mode]').forEach(x => x.classList.toggle('on', x === b));
        $('ov-text').textContent = mode === 'flick' ? 'One target at a time and it shrinks. Too slow counts as a miss.' : '30 seconds. Three targets. Center hits score more.';
        loadBoard(); showBest();
    }));
    $('start').addEventListener('click', start);
    loadBoard(); showBest();
})();
