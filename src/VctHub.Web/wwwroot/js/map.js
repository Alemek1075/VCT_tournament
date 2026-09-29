// VCT map: teams + venues, filters, season timeline, travel lines and small charts.
(async function () {
    const COLORS = { americas: '#ff4655', emea: '#3fd1b4', pacific: '#f2c14e', china: '#9d8cff', international: '#ece8e1' };
    const NAMES = { americas: 'Americas', emea: 'EMEA', pacific: 'Pacific', china: 'China' };
    const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

    const map = L.map('map', { worldCopyJump: true, minZoom: 2 }).setView([30, 20], 2);
    // standard OSM tiles, darkened in CSS (.osm-dark) to match the site
    L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
        attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors',
        maxZoom: 18, className: 'osm-dark',
    }).addTo(map);

    const { teams, events } = await fetch('/api/map').then(r => r.json());
    const eventById = Object.fromEntries(events.map(e => [e.id, e]));

    // ---- layers
    const teamLayer = L.markerClusterGroup({ showCoverageOnHover: false, maxClusterRadius: 45 });
    const eventLayer = L.layerGroup();
    const travelLayer = L.layerGroup().addTo(map);
    map.addLayer(teamLayer);
    map.addLayer(eventLayer);

    const teamMarkers = teams.map(t => {
        const icon = L.divIcon({
            className: '', iconSize: [38, 38], iconAnchor: [19, 19],
            html: `<div class="team-pin" style="--c:${COLORS[t.region]}"><img src="${esc(t.logoUrl)}" alt=""></div>`,
        });
        const m = L.marker([t.lat, t.lng], { icon, title: t.name });
        m.team = t;
        m.bindPopup(() => teamPopup(t), { minWidth: 260 });
        m.on('popupopen', () => { drawRecordChart(t); drawTravel(t); });
        m.on('popupclose', () => travelLayer.clearLayers());
        return m;
    });

    const venueMarkers = events.map(e => {
        const icon = L.divIcon({ className: '', iconSize: [22, 22], iconAnchor: [11, 11], html: '<div class="venue-pin"></div>' });
        const m = L.marker([e.lat, e.lng], { icon, title: e.name, zIndexOffset: 1000 });
        m.event = e;
        m.bindPopup(`<div class="pop"><img src="${esc(e.logoUrl)}" alt="" width="80" height="80" style="object-fit:contain">
            <div><h3>${esc(e.name)}</h3><div class="muted">${esc(e.venue)}</div>
            <div class="muted">${e.startDate} → ${e.endDate}</div>
            <div>${e.teams.length} teams · ${e.matches} matches${e.prizePool ? ' · $' + Number(e.prizePool).toLocaleString() : ''}</div>
            <a class="btn btn-red btn-sm" href="/Tournaments/Details/${e.id}">Open event</a></div></div>`);
        return m;
    });
    venueMarkers.forEach(m => eventLayer.addLayer(m));

    function teamPopup(t) {
        return `<div class="pop">
            <canvas id="rec-${t.id}" width="90" height="90" aria-label="Win/loss chart"></canvas>
            <div><h3>${esc(t.name)}</h3>
            <div class="muted">${esc(t.city ?? '')}${t.country ? ', ' + esc(t.country) : ''} · ${NAMES[t.region]}</div>
            <div><b>${t.wins}–${t.losses}</b> series · avg rating <b>${t.rating}</b></div>
            <div class="muted">${t.events.length} events played (lines on the map)</div>
            <a class="btn btn-red btn-sm" href="/team/${esc(t.slug)}">Team page</a></div></div>`;
    }

    // F1: doughnut inside the popup
    function drawRecordChart(t) {
        const el = document.getElementById(`rec-${t.id}`);
        if (!el) return;
        new Chart(el, {
            type: 'doughnut',
            data: { labels: ['Wins', 'Losses'], datasets: [{ data: [t.wins, t.losses], backgroundColor: ['#3fd1b4', '#2a3a49'], borderWidth: 0 }] },
            options: { responsive: false, cutout: '62%', plugins: { legend: { display: false }, tooltip: { enabled: true } }, animation: { duration: 400 } },
            plugins: [{ id: 'center', afterDraw(c) {
                const { ctx, chartArea: a } = c; ctx.save();
                ctx.fillStyle = '#ece8e1'; ctx.font = '700 16px "Barlow Condensed"'; ctx.textAlign = 'center'; ctx.textBaseline = 'middle';
                const pct = t.wins + t.losses ? Math.round(100 * t.wins / (t.wins + t.losses)) : 0;
                ctx.fillText(pct + '%', (a.left + a.right) / 2, (a.top + a.bottom) / 2); ctx.restore();
            } }],
        });
    }

    // travel lines from home base to every venue the team played at, drawn one after another
    function drawTravel(t) {
        travelLayer.clearLayers();
        const stops = t.events.map(id => eventById[id]).filter(Boolean).sort((a, b) => a.startDate.localeCompare(b.startDate));
        stops.forEach((e, i) => setTimeout(() => {
            const line = L.polyline(arc([t.lat, t.lng], [e.lat, e.lng]), { color: COLORS[t.region], weight: 2, opacity: .8, dashArray: '6 6' });
            travelLayer.addLayer(line);
        }, i * 180));
    }

    // simple great-circle-ish curve so lines don't overlap each other
    function arc(a, b) {
        const pts = [], n = 32;
        const mid = [(a[0] + b[0]) / 2, (a[1] + b[1]) / 2];
        const dx = b[1] - a[1], dy = b[0] - a[0];
        const ctrl = [mid[0] + dx * 0.15, mid[1] - dy * 0.15];
        for (let i = 0; i <= n; i++) {
            const s = i / n;
            pts.push([(1 - s) ** 2 * a[0] + 2 * (1 - s) * s * ctrl[0] + s * s * b[0], (1 - s) ** 2 * a[1] + 2 * (1 - s) * s * ctrl[1] + s * s * b[1]]);
        }
        return pts;
    }

    // ---- filters
    const state = { regions: new Set(Object.keys(NAMES)), q: '', teams: true, events: true, winning: false };
    const regionChart = new Chart(document.getElementById('region-chart'), {
        type: 'bar',
        data: { labels: [], datasets: [{ data: [], backgroundColor: [], borderWidth: 0 }] },
        options: {
            indexAxis: 'y', animation: { duration: 350 },
            scales: { x: { min: 0, max: 100, ticks: { color: '#8b978f', callback: v => v + '%' }, grid: { color: '#2a3a49' } },
                      y: { ticks: { color: '#ece8e1', font: { family: 'Barlow Condensed', size: 13, weight: 600 } }, grid: { display: false } } },
            plugins: { legend: { display: false }, tooltip: { callbacks: { label: c => ` ${c.raw}% series won` } } },
        },
    });

    function apply() {
        const q = state.q.toLowerCase();
        const visible = teamMarkers.filter(m => {
            const t = m.team;
            return state.regions.has(t.region)
                && (!q || t.name.toLowerCase().includes(q) || t.tag.toLowerCase().includes(q))
                && (!state.winning || t.wins > t.losses);
        });
        teamLayer.clearLayers();
        if (state.teams) teamLayer.addLayers(visible);
        state.events ? map.addLayer(eventLayer) : map.removeLayer(eventLayer);
        venueMarkers.forEach(m => m.setOpacity(state.regions.has(m.event.region) || m.event.region === 'international' ? 1 : 0.15));

        const byRegion = {};
        visible.forEach(m => { const r = byRegion[m.team.region] ??= { w: 0, l: 0 }; r.w += m.team.wins; r.l += m.team.losses; });
        const keys = Object.keys(byRegion);
        regionChart.data.labels = keys.map(k => NAMES[k]);
        regionChart.data.datasets[0].data = keys.map(k => Math.round(100 * byRegion[k].w / Math.max(1, byRegion[k].w + byRegion[k].l)));
        regionChart.data.datasets[0].backgroundColor = keys.map(k => COLORS[k]);
        regionChart.update();
        document.getElementById('visible-count').textContent = `${visible.length} of ${teams.length} teams shown`;

        if (q && visible.length === 1) {
            map.flyTo(visible[0].getLatLng(), 6, { duration: 1 });
            setTimeout(() => teamLayer.zoomToShowLayer(visible[0], () => visible[0].openPopup()), 1100);
        }
    }

    document.querySelectorAll('#region-filter .chip').forEach(b => b.addEventListener('click', () => {
        b.classList.toggle('on');
        b.classList.contains('on') ? state.regions.add(b.dataset.region) : state.regions.delete(b.dataset.region);
        apply();
    }));
    let typing;
    document.getElementById('map-search').addEventListener('input', e => { clearTimeout(typing); typing = setTimeout(() => { state.q = e.target.value.trim(); apply(); }, 250); });
    document.getElementById('layer-teams').addEventListener('change', e => { state.teams = e.target.checked; apply(); });
    document.getElementById('layer-events').addEventListener('change', e => { state.events = e.target.checked; apply(); });
    document.getElementById('only-winning').addEventListener('change', e => { state.winning = e.target.checked; apply(); });

    // ---- season timeline
    const day = 864e5;
    const start = Math.min(...events.map(e => Date.parse(e.startDate)));
    const end = Math.max(...events.map(e => Date.parse(e.endDate)));
    const slider = document.getElementById('timeline');
    slider.max = Math.round((end - start) / day);
    slider.value = Math.min(slider.max, Math.round((Date.now() - start) / day));

    function showDate() {
        const now = start + slider.value * day;
        document.getElementById('timeline-date').textContent = new Date(now).toLocaleDateString([], { month: 'short', day: 'numeric' });
        const running = [];
        venueMarkers.forEach(m => {
            const on = Date.parse(m.event.startDate) <= now && now <= Date.parse(m.event.endDate) + day;
            m.getElement()?.firstElementChild?.classList.toggle('active', on);
            if (on) running.push(m.event.name);
        });
        document.getElementById('timeline-note').textContent = running.length ? 'Running: ' + running.join(', ') : 'No event on this day.';
    }
    slider.addEventListener('input', showDate);

    let timer = null;
    document.getElementById('play').addEventListener('click', e => {
        if (timer) { clearInterval(timer); timer = null; e.target.textContent = '▶'; return; }
        if (+slider.value >= +slider.max) slider.value = 0;
        e.target.textContent = '❚❚';
        timer = setInterval(() => {
            slider.value = +slider.value + 2;
            showDate();
            if (+slider.value >= +slider.max) { clearInterval(timer); timer = null; e.target.textContent = '▶'; }
        }, 60);
    });

    apply();
    map.on('zoomend moveend', showDate);
    showDate();
})();
