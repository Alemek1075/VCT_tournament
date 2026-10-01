// C20: web3.js front end for the PickEm contract (contracts/contracts/PickEm.sol).
// Reads go through a public RPC so the page works without a wallet; picks are sent with MetaMask.
(async () => {
    const cfg = JSON.parse(document.getElementById('pickem-config').textContent);
    const $ = id => document.getElementById(id);
    const esc = s => String(s ?? '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
    const short = a => a.slice(0, 6) + '…' + a.slice(-4);
    const msg = html => { $('pk-msg').hidden = false; $('pk-msg').innerHTML = html; };
    const txLink = h => cfg.explorer ? `<a href="${cfg.explorer}/tx/${h}" target="_blank" rel="noopener">${short(h)}</a>` : `<code>${short(h)}</code>`;

    if (!cfg.address) { $('pk-status').textContent = 'The contract is not deployed on this server yet (PICKEM_ADDRESS is empty).'; return; }

    const abi = await fetch('/js/pickem-abi.json').then(r => r.json());
    const reader = new Web3(cfg.rpc);
    const ro = new reader.eth.Contract(abi, cfg.address);
    let account = null, rw = null;

    // custom Solidity errors come back decoded by web3 when the ABI lists them
    const reason = e => {
        const name = e?.cause?.errorName ?? e?.innerError?.errorName ?? e?.data?.errorName;
        const text = { NotOpen: 'This match is not open for picks.', Locked: 'Too late, the match has started.',
            AlreadyPicked: 'You already picked this match.', BadTeam: 'Pick team A or team B.' }[name];
        if (e?.code === 4001 || e?.code === 100) return 'You rejected the transaction in MetaMask.';
        return text ?? esc(e?.cause?.message ?? e?.message ?? String(e));
    };

    const [up, done] = await Promise.all(['Upcoming', 'Completed'].map(s =>
        fetch(`/api/matches?status=${s}&limit=25`).then(r => r.json()).then(p => p.value)));
    const matches = [...up, ...done];

    async function render() {
        let games;
        try {
            games = await Promise.all(matches.map(m => ro.methods.games(m.id).call()));
        } catch (e) {
            $('pk-status').textContent = `Can't reach the chain at ${cfg.rpc}: ${e.message}`;
            return;
        }
        const mine = account ? await Promise.all(matches.map(m => ro.methods.pickOf(m.id, account).call())) : [];
        const now = Date.now() / 1000;
        const rows = [];
        const fans = new Set();

        for (const [i, m] of matches.entries()) {
            const g = games[i], status = Number(g.status);
            if (status === 0) continue; // match was never opened in the contract
            const a = Number(g.picksA), b = Number(g.picksB), total = a + b;
            const my = Number(mine[i] ?? 0), winner = Number(g.winner);
            const open = status === 1 && now < Number(g.lockTime);
            const state = status === 2 ? `Winner: ${esc(winner === 1 ? m.teamA?.tag : m.teamB?.tag)}${my ? (my === winner ? ' · you got it ✔' : ' · missed') : ''}`
                : status === 3 ? 'Cancelled' : open ? `Picks close ${new Date(Number(g.lockTime) * 1000).toLocaleString()}` : 'Locked, waiting for the result';
            const btn = (team, t) => `<button class="btn btn-ghost btn-sm ${my === team ? 'on' : ''}" data-id="${m.id}" data-team="${team}"
                ${!open || !account || my ? 'disabled' : ''}>${esc(t?.tag ?? '?')}</button>`;
            rows.push(`<div class="pk-row">
                <div>
                    <div class="pk-teams"><img src="${esc(m.teamA?.logoUrl)}" alt="">${esc(m.teamA?.name)} <span class="muted">vs</span>
                        <img src="${esc(m.teamB?.logoUrl)}" alt="">${esc(m.teamB?.name)}</div>
                    <div class="muted small">${esc(m.tournament)} · ${state}</div>
                    <div class="pk-bar" title="${a} vs ${b} picks"><span style="width:${total ? a / total * 100 : 0}%"></span></div>
                    <div class="muted small">${total} pick${total === 1 ? '' : 's'}${total ? ` · ${Math.round(a / total * 100)}% ${esc(m.teamA?.tag)}` : ''}</div>
                </div>
                <div class="pk-actions">${btn(1, m.teamA)}${btn(2, m.teamB)}</div>
            </div>`);
            if (total) (await ro.methods.pickersOf(m.id).call()).forEach(f => fans.add(f));
        }
        $('pk-list').innerHTML = rows.join('') || '<p class="muted p-3 m-0">No matches are open on chain yet. The owner runs <code>scripts/sync.js</code> to open them.</p>';

        const board = (await Promise.all([...fans].map(async f => [f, Number(await ro.methods.points(f).call())])))
            .filter(([, p]) => p > 0).sort((x, y) => y[1] - x[1]).slice(0, 10);
        if (board.length) $('pk-board').innerHTML = board.map(([f, p], i) =>
            `<tr><td class="muted">${i + 1}</td><td><code>${short(f)}</code>${account && f.toLowerCase() === account.toLowerCase() ? ' (you)' : ''}</td><td class="text-end fw-bold">${p}</td></tr>`).join('');
        if (account) $('pk-account').innerHTML = `<code>${esc(account)}</code><br>${Number(await ro.methods.points(account).call())} point(s)`;
    }

    async function connect() {
        if (!window.ethereum) { msg('Install <a href="https://metamask.io" target="_blank" rel="noopener">MetaMask</a> to make picks.'); return; }
        const hex = '0x' + cfg.chainId.toString(16);
        try {
            [account] = await window.ethereum.request({ method: 'eth_requestAccounts' });
            try {
                await window.ethereum.request({ method: 'wallet_switchEthereumChain', params: [{ chainId: hex }] });
            } catch (e) {
                if (e.code !== 4902) throw e; // 4902 = MetaMask doesn't know this chain yet
                await window.ethereum.request({ method: 'wallet_addEthereumChain', params: [{
                    chainId: hex, chainName: cfg.chainName, rpcUrls: [cfg.rpc],
                    nativeCurrency: { name: 'Ether', symbol: 'ETH', decimals: 18 },
                    blockExplorerUrls: cfg.explorer ? [cfg.explorer] : null }] });
            }
            rw = new new Web3(window.ethereum).eth.Contract(abi, cfg.address);
            $('pk-connect').textContent = 'Connected';
            $('pk-connect').disabled = true;
            await render();
        } catch (e) { msg(reason(e)); }
    }

    $('pk-connect').addEventListener('click', connect);
    window.ethereum?.on?.('accountsChanged', () => location.reload());
    window.ethereum?.on?.('chainChanged', () => location.reload());

    $('pk-list').addEventListener('click', async e => {
        const b = e.target.closest('button[data-id]');
        if (!b || !rw) return;
        const id = +b.dataset.id, team = +b.dataset.team;
        b.disabled = true;
        try {
            await rw.methods.pick(id, team).call({ from: account }); // dry run first: shows the revert reason without paying gas
            await rw.methods.pick(id, team).send({ from: account })
                .on('transactionHash', h => msg(`Transaction ${txLink(h)} sent, waiting for a block…`));
            msg('Pick saved on chain ✔');
            if (typeof track === 'function') track('pickem_pick', { match_id: id });
            await render();
        } catch (err) { msg(reason(err)); b.disabled = false; }
    });

    await render();
})();
