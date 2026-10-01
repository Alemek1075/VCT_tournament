# Pick'em smart contract (C20)

`contracts/PickEm.sol` keeps match predictions on an Ethereum chain. The website page `/pickem` reads it with web3.js and sends picks through MetaMask.

How it works:
- the owner opens upcoming matches (`openMatches`) with a lock time = match start;
- any wallet can pick team A or B once per match (`pick`), until the lock time;
- after the match the owner posts the winner (`settle`), every correct pick gets 1 point;
- `cancel` closes a match without points. Errors are custom (`NotOpen`, `Locked`, `AlreadyPicked`, ...), the page turns them into readable messages.

No ETH is staked, so it's a game, not betting.

## Tests

```
npm ci
npx hardhat test
```

7 tests: opening, owner-only actions, one pick per wallet, lock time, settling and points, cancel, ownership transfer.

## Run locally

```
npx hardhat node                                         # local chain on :8545, chain id 31337
npx hardhat run scripts/deploy.js --network localhost    # writes deployments/localhost.json and the ABI for the site
npx hardhat run scripts/sync.js --network localhost      # opens upcoming matches from the site, settles finished ones
npx hardhat run scripts/demo.js --network localhost      # optional: 5 test wallets make picks
```

Then put the printed address into `.env` as `PICKEM_ADDRESS=...` and restart the web container. To pick from the browser, import one of the test accounts that `hardhat node` prints into MetaMask; the page adds the local network itself.

## Sepolia (public testnet)

1. Make a new wallet only for this (never your main one) and put its key in `.env` as `DEPLOYER_KEY=...`.
2. Get free test ETH from a Sepolia faucet.
3. `npx hardhat run scripts/deploy.js --network sepolia`
4. On the server set `PICKEM_ADDRESS` and `PICKEM_CHAIN_ID=11155111`. The page then reads through a public Sepolia RPC and links transactions to Etherscan.
5. Run `scripts/sync.js --network sepolia` with `SITE_URL=https://vct-hub.onrender.com` whenever matches should be opened or settled.
