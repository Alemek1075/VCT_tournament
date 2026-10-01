// npx hardhat run scripts/demo.js --network localhost
// Local demo only: a few test accounts pick the open matches, so the page has data to show.
const fs = require('fs')
const path = require('path')
const { ethers, network } = require('hardhat')

async function main() {
  if (network.name !== 'localhost') throw new Error('demo picks are for the local chain only')
  const { address } = JSON.parse(fs.readFileSync(path.join(__dirname, '..', 'deployments', 'localhost.json')))
  const pickem = await ethers.getContractAt('PickEm', address)
  const fans = (await ethers.getSigners()).slice(1, 6)
  const ids = (await pickem.gameCount()) > 0n ? await openIds(pickem) : []
  for (const id of ids)
    for (const [i, fan] of fans.entries()) {
      if ((await pickem.pickOf(id, fan.address)) !== 0n) continue
      await (await pickem.connect(fan).pick(id, i % 3 === 0 ? 2 : 1)).wait()
    }
  console.log(`${fans.length} fans picked ${ids.length} matches`)
}

async function openIds(pickem) {
  const ids = []
  for (let i = 0n; i < await pickem.gameCount(); i++) {
    const id = await pickem.gameIds(i)
    if ((await pickem.games(id)).status === 1n) ids.push(id)
  }
  return ids
}

main().catch((e) => { console.error(e); process.exit(1) })
