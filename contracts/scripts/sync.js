// npx hardhat run scripts/sync.js --network localhost|sepolia
// Opens the site's upcoming matches in the contract and settles the finished ones.
// SITE_URL defaults to the local docker stack.
const fs = require('fs')
const path = require('path')
const { ethers, network } = require('hardhat')

const site = process.env.SITE_URL || 'http://localhost:8080'

async function page(status, limit) {
  const r = await fetch(`${site}/api/matches?status=${status}&limit=${limit}`)
  if (!r.ok) throw new Error(`${status}: HTTP ${r.status}`)
  return (await r.json()).value
}

async function main() {
  const { address } = JSON.parse(fs.readFileSync(path.join(__dirname, '..', 'deployments', `${network.name}.json`)))
  const pickem = await ethers.getContractAt('PickEm', address)
  const now = Math.floor(Date.now() / 1000)

  // 1) open upcoming matches that aren't in the contract yet
  const upcoming = (await page('Upcoming', 30)).filter((m) => Date.parse(m.scheduledAt) / 1000 > now + 60)
  const fresh = []
  for (const m of upcoming) if ((await pickem.games(m.id)).status === 0n) fresh.push(m)
  if (fresh.length) {
    const tx = await pickem.openMatches(fresh.map((m) => m.id), fresh.map((m) => Math.floor(Date.parse(m.scheduledAt) / 1000)))
    await tx.wait()
  }
  console.log(`opened ${fresh.length} matches`)

  // 2) settle open matches the site already has a result for
  let settled = 0
  for (const m of await page('Completed', 100)) {
    const g = await pickem.games(m.id)
    if (g.status !== 1n || m.scoreA === m.scoreB) continue
    await (await pickem.settle(m.id, m.scoreA > m.scoreB ? 1 : 2)).wait()
    settled++
  }
  console.log(`settled ${settled} matches`)
}

main().catch((e) => { console.error(e); process.exit(1) })
