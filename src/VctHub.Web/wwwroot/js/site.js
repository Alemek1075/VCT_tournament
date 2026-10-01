// Site-wide interactions: nav indicator, scroll reveal, card spotlight, magnetic buttons,
// countdowns, toasts and the Ctrl+K command palette. Everything here is optional sugar:
// pages work the same with JS off or with reduced motion.
(() => {
    const motion = !matchMedia('(prefers-reduced-motion: reduce)').matches;
    const fine = matchMedia('(pointer: fine)').matches;
    const $$ = (sel, root = document) => [...root.querySelectorAll(sel)];
    const esc = s => String(s ?? '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));

    // ---- active nav link + gliding underline
    const nav = document.querySelector('.topbar .navbar-nav');
    if (nav) {
        const path = location.pathname.toLowerCase();
        const links = $$(':scope > li > .nav-link:not(.dropdown-toggle)', nav);
        const active = links.find(a => {
            const href = a.getAttribute('href').toLowerCase();
            return href !== '/' && (path === href || path.startsWith(href + '/') || (href === '/teams' && path.startsWith('/team/')));
        });
        active?.classList.add('active');

        const ink = document.createElement('span');
        ink.className = 'nav-ink';
        nav.append(ink);
        const moveTo = el => {
            if (!el) { ink.style.opacity = 0; return; }
            ink.style.opacity = 1;
            ink.style.transform = `translateX(${el.offsetLeft}px) scaleX(${el.offsetWidth})`;
        };
        // first placement without animation
        ink.style.transition = 'none';
        moveTo(active);
        requestAnimationFrame(() => ink.style.transition = '');
        $$(':scope > li > .nav-link', nav).forEach(a => a.addEventListener('pointerenter', () => moveTo(a)));
        nav.addEventListener('pointerleave', () => moveTo(active));
        addEventListener('resize', () => moveTo(active));
    }

    // ---- top bar: glass after scrolling, red read-progress line
    const bar = document.querySelector('.topbar');
    if (bar) {
        let ticking = false;
        const onScroll = () => {
            ticking = false;
            const y = scrollY, max = document.documentElement.scrollHeight - innerHeight;
            bar.classList.toggle('scrolled', y > 8);
            bar.style.setProperty('--progress', max > 200 ? Math.min(1, y / max).toFixed(4) : 0);
        };
        addEventListener('scroll', () => { if (!ticking) { ticking = true; requestAnimationFrame(onScroll); } }, { passive: true });
        onScroll();
    }

    // ---- scroll reveal with a small stagger per container
    if (motion && 'IntersectionObserver' in window) {
        const targets = $$('.panel, .team-card, .roster-card, .plan-card, .next-match, .results-list, [data-reveal]')
            .filter(el => !el.closest('.hero, .topbar, .cmdk, .modal, .dropdown-menu') && el.getBoundingClientRect().top > innerHeight * .9);
        if (targets.length) {
            document.documentElement.classList.add('rv');
            const seen = new Map();
            targets.forEach(el => {
                el.setAttribute('data-reveal', '');
                const parent = el.parentElement.closest('.row, .roster-grid, section') ?? el.parentElement;
                const n = seen.get(parent) ?? 0;
                seen.set(parent, n + 1);
                el.style.setProperty('--d', Math.min(n, 8));
            });
            const io = new IntersectionObserver(entries => entries.forEach(e => {
                if (!e.isIntersecting) return;
                const el = e.target;
                el.classList.add('in');
                io.unobserve(el);
                // hand the element back to its own hover transitions once it has arrived
                setTimeout(() => { el.removeAttribute('data-reveal'); el.classList.remove('in'); }, 1400);
            }), { rootMargin: '0px 0px -8% 0px' });
            targets.forEach(el => io.observe(el));
        }
    }

    // ---- cursor spotlight on cards
    if (fine) {
        document.addEventListener('pointermove', e => {
            const card = e.target.closest?.('.team-card, .roster-card, .plan-card');
            if (!card) return;
            const r = card.getBoundingClientRect();
            card.style.setProperty('--mx', `${e.clientX - r.left}px`);
            card.style.setProperty('--my', `${e.clientY - r.top}px`);
        }, { passive: true });
    }

    // ---- magnetic buttons: pulled a few px toward the cursor
    if (fine && motion) {
        $$('[data-magnetic]').forEach(el => {
            const pull = 0.3;
            el.style.transition += ', translate .5s cubic-bezier(.16,1,.3,1)';
            el.addEventListener('pointermove', e => {
                const r = el.getBoundingClientRect();
                el.style.translate = `${(e.clientX - r.left - r.width / 2) * pull}px ${(e.clientY - r.top - r.height / 2) * pull}px`;
            });
            el.addEventListener('pointerleave', () => el.style.translate = '0 0');
        });
    }

    // ---- hero parallax on the red slash
    const slash = document.querySelector('.hero-slash');
    if (slash && fine && motion) {
        document.querySelector('.hero').addEventListener('pointermove', e => {
            slash.style.setProperty('--px', `${(e.clientX / innerWidth - .5) * -24}px`);
        });
    }

    // ---- countdowns: <div data-countdown="ISO date">
    $$('[data-countdown]').forEach(el => {
        const target = Date.parse(el.dataset.countdown);
        const cell = (v, l) => `<span><b>${String(v).padStart(2, '0')}</b><small>${l}</small></span>`;
        const tick = () => {
            let s = Math.max(0, Math.floor((target - Date.now()) / 1000));
            if (s === 0) { el.innerHTML = '<span class="tag soon">Starting soon</span>'; return; }
            const d = Math.floor(s / 86400); s %= 86400;
            const h = Math.floor(s / 3600); s %= 3600;
            const m = Math.floor(s / 60);
            el.innerHTML = (d ? cell(d, 'days') : '') + cell(h, 'hrs') + cell(m, 'min') + cell(s % 60, 'sec');
            setTimeout(tick, 1000);
        };
        tick();
    });

    // ---- count-up numbers: <b data-count="123.4">
    const counters = $$('[data-count]');
    if (counters.length && motion && 'IntersectionObserver' in window) {
        const io = new IntersectionObserver(entries => entries.forEach(e => {
            if (!e.isIntersecting) return;
            io.unobserve(e.target);
            const el = e.target, end = parseFloat(el.dataset.count), dec = (el.dataset.count.split('.')[1] ?? '').length;
            const t0 = performance.now(), dur = 900;
            const step = now => {
                const p = Math.min(1, (now - t0) / dur), k = 1 - Math.pow(1 - p, 4);
                el.textContent = (end * k).toFixed(dec);
                if (p < 1) requestAnimationFrame(step);
            };
            requestAnimationFrame(step);
        }));
        counters.forEach(el => io.observe(el));
    }

    // ---- toasts
    $$('.toast-dock .flash').forEach(t => {
        const close = () => { t.classList.add('out'); setTimeout(() => t.remove(), 300); };
        t.querySelector('button')?.addEventListener('click', close);
        let timer = setTimeout(close, 6000);
        t.addEventListener('pointerenter', () => clearTimeout(timer));
        t.addEventListener('pointerleave', () => timer = setTimeout(close, 2500));
    });

    // ---- command palette
    const dlg = document.getElementById('cmdk');
    if (dlg && dlg.showModal) {
        const input = document.getElementById('cmdk-q'), list = document.getElementById('cmdk-list'), engine = document.getElementById('cmdk-engine');
        const pages = [
            ['Live scores', '/Live', 'LV'], ['Matches', '/Matches', 'MT'], ['Events', '/Tournaments', 'EV'], ['Teams', '/Teams', 'TM'],
            ['Players', '/Players', 'PL'], ['Map', '/Map', 'MP'], ["Pick'em", '/pickem', 'PK'], ['Aim trainer', '/aim', 'AM'],
            ['Board', '/board', 'BD'], ['AI analyst', '/agent', 'AI'], ['React app', '/spa/', 'JS'], ['Full search page', '/search', 'SR'],
        ].map(([title, url, ic]) => ({ title, url, ic, type: 'Page' }));
        let items = [], sel = 0, ctl = null, timer = 0;

        const draw = (groups) => {
            items = groups.flatMap(g => g.items);
            sel = Math.min(sel, Math.max(0, items.length - 1));
            if (!items.length) { list.innerHTML = '<li class="cmdk-empty">Nothing found. Press Enter for the full search.</li>'; return; }
            let i = 0;
            list.innerHTML = groups.filter(g => g.items.length).map(g => `<li class="cmdk-group">${esc(g.name)}</li>` + g.items.map(it => `
                <li class="cmdk-item" role="option" id="cmdk-${i}" aria-selected="${i === sel}" data-i="${i++}">
                    <a href="${esc(it.url)}">${it.image ? `<img src="${esc(it.image)}" alt="">` : `<span class="ic">${esc(it.ic ?? it.type[0])}</span>`}
                    <span>${esc(it.title)}${it.sub ? `<small>${esc(it.sub)}</small>` : ''}</span><span class="type">${esc(it.type)}</span></a>
                </li>`).join('')).join('');
            input.setAttribute('aria-activedescendant', `cmdk-${sel}`);
        };
        const select = i => {
            sel = (i + items.length) % items.length;
            $$('.cmdk-item', list).forEach(li => li.setAttribute('aria-selected', String(+li.dataset.i === sel)));
            document.getElementById(`cmdk-${sel}`)?.scrollIntoView({ block: 'nearest' });
            input.setAttribute('aria-activedescendant', `cmdk-${sel}`);
        };
        const search = async q => {
            const local = pages.filter(p => p.title.toLowerCase().includes(q.toLowerCase()));
            if (q.length < 2) { engine.textContent = ''; draw([{ name: 'Pages', items: local.length ? local : pages }]); return; }
            ctl?.abort(); ctl = new AbortController();
            list.innerHTML = '<li class="cmdk-empty"><div class="skeleton" style="height:28px;margin-bottom:6px"></div><div class="skeleton" style="height:28px"></div></li>';
            try {
                const r = await fetch(`/api/search?q=${encodeURIComponent(q)}&limit=8`, { signal: ctl.signal });
                const res = r.ok ? await r.json() : { hits: [] };
                engine.textContent = res.engine ? `${res.engine} · ${res.tookMs} ms` : '';
                sel = 0;
                draw([{ name: 'Results', items: res.hits.map(h => ({ title: h.title, sub: h.subtitle, image: h.image, url: h.url, type: h.type })) }, { name: 'Pages', items: local }]);
            } catch (e) { if (e.name !== 'AbortError') draw([{ name: 'Pages', items: local }]); }
        };
        const open = () => { if (dlg.open) return; dlg.showModal(); input.value = ''; sel = 0; search(''); input.focus(); };

        $$('[data-cmdk]').forEach(a => a.addEventListener('click', e => { e.preventDefault(); open(); }));
        document.addEventListener('keydown', e => {
            const typing = /^(INPUT|TEXTAREA|SELECT)$/.test(e.target.tagName) || e.target.isContentEditable;
            if ((e.key === 'k' || e.key === 'K') && (e.ctrlKey || e.metaKey)) { e.preventDefault(); dlg.open ? dlg.close() : open(); }
            else if (e.key === '/' && !typing && !dlg.open) { e.preventDefault(); open(); }
        });
        input.addEventListener('input', () => { clearTimeout(timer); timer = setTimeout(() => search(input.value.trim()), 140); });
        input.addEventListener('keydown', e => {
            if (e.key === 'ArrowDown') { e.preventDefault(); select(sel + 1); }
            else if (e.key === 'ArrowUp') { e.preventDefault(); select(sel - 1); }
            else if (e.key === 'Enter') {
                e.preventDefault();
                const q = input.value.trim();
                location.href = items[sel]?.url ?? (q ? `/search?q=${encodeURIComponent(q)}` : '/search');
            }
        });
        list.addEventListener('pointermove', e => { const li = e.target.closest('.cmdk-item'); if (li && +li.dataset.i !== sel) select(+li.dataset.i); });
        dlg.addEventListener('click', e => { if (e.target === dlg) dlg.close(); }); // click on the backdrop
    }
})();
