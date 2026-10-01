// npx hardhat run scripts/deploy.js --network localhost|sepolia
// Deploys PickEm, saves the address in deployments/<network>.json and the ABI for the web page.
const fs = require('fs')
const path = require('path')
const { ethers, network, artifacts } = require('hardhat')

async function main() {
  const [deployer] = await ethers.getSigners()
  console.log(`deploying from ${deployer.address} on ${network.name}`)
  const pickem = await ethers.deployContract('PickEm')
  await pickem.waitForDeployment()
  const address = await pickem.getAddress()
  const { chainId } = await ethers.provider.getNetwork()
  console.log(`PickEm deployed at ${address} (chain ${chainId})`)

  fs.mkdirSync(path.join(__dirname, '..', 'deployments'), { recursive: true })
  fs.writeFileSync(path.join(__dirname, '..', 'deployments', `${network.name}.json`),
    JSON.stringify({ address, chainId: Number(chainId), deployer: deployer.address, at: new Date().toISOString() }, null, 2))

  const { abi } = await artifacts.readArtifact('PickEm')
  fs.writeFileSync(path.join(__dirname, '..', '..', 'src', 'VctHub.Web', 'wwwroot', 'js', 'pickem-abi.json'), JSON.stringify(abi))

  if (process.env.PICKEM_OWNER) {
    await (await pickem.transferOwnership(process.env.PICKEM_OWNER)).wait()
    console.log(`ownership moved to ${process.env.PICKEM_OWNER}`)
  }
}

main().catch((e) => { console.error(e); process.exit(1) })
