require('@nomicfoundation/hardhat-toolbox')
require('dotenv').config({ path: require('path').join(__dirname, '..', '.env') })

// DEPLOYER_KEY is a throwaway testnet-only wallet (never a real one), kept in the git-ignored .env.
const accounts = process.env.DEPLOYER_KEY ? [process.env.DEPLOYER_KEY] : []

module.exports = {
  solidity: '0.8.24',
  networks: {
    localhost: { url: 'http://127.0.0.1:8545' },
    sepolia: {
      url: process.env.SEPOLIA_RPC_URL || 'https://ethereum-sepolia-rpc.publicnode.com',
      accounts,
    },
  },
}
