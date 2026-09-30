const { expect } = require('chai')
const { ethers } = require('hardhat')
const { time } = require('@nomicfoundation/hardhat-network-helpers')

describe('PickEm', () => {
  async function setup() {
    const [owner, alice, bob] = await ethers.getSigners()
    const pickem = await ethers.deployContract('PickEm')
    const lock = (await time.latest()) + 3600
    await pickem.openMatches([101, 102], [lock, lock])
    return { pickem, owner, alice, bob, lock }
  }

  it('opens matches and ignores duplicates', async () => {
    const { pickem, lock } = await setup()
    await pickem.openMatches([101, 103], [lock, lock])
    expect(await pickem.gameCount()).to.equal(3)
    expect((await pickem.games(101)).status).to.equal(1) // Open
  })

  it('only the owner can open, settle and cancel', async () => {
    const { pickem, alice } = await setup()
    await expect(pickem.connect(alice).openMatches([200], [1])).to.be.revertedWithCustomError(pickem, 'NotOwner')
    await expect(pickem.connect(alice).settle(101, 1)).to.be.revertedWithCustomError(pickem, 'NotOwner')
    await expect(pickem.connect(alice).cancel(101)).to.be.revertedWithCustomError(pickem, 'NotOwner')
  })

  it('records one pick per fan and counts them', async () => {
    const { pickem, alice, bob } = await setup()
    await expect(pickem.connect(alice).pick(101, 1)).to.emit(pickem, 'Picked').withArgs(101, alice.address, 1)
    await pickem.connect(bob).pick(101, 2)
    await expect(pickem.connect(alice).pick(101, 2)).to.be.revertedWithCustomError(pickem, 'AlreadyPicked')

    const g = await pickem.games(101)
    expect(g.picksA).to.equal(1)
    expect(g.picksB).to.equal(1)
    expect(await pickem.pickOf(101, alice.address)).to.equal(1)
  })

  it('rejects bad teams, unknown matches and picks after lock', async () => {
    const { pickem, alice, lock } = await setup()
    await expect(pickem.connect(alice).pick(101, 3)).to.be.revertedWithCustomError(pickem, 'BadTeam')
    await expect(pickem.connect(alice).pick(999, 1)).to.be.revertedWithCustomError(pickem, 'NotOpen')
    await time.increaseTo(lock)
    await expect(pickem.connect(alice).pick(101, 1)).to.be.revertedWithCustomError(pickem, 'Locked')
  })

  it('settling gives a point to correct picks only', async () => {
    const { pickem, alice, bob } = await setup()
    await pickem.connect(alice).pick(101, 1)
    await pickem.connect(bob).pick(101, 2)
    await pickem.connect(alice).pick(102, 2)

    await expect(pickem.settle(101, 1)).to.emit(pickem, 'MatchSettled').withArgs(101, 1)
    await pickem.settle(102, 2)
    expect(await pickem.points(alice.address)).to.equal(2)
    expect(await pickem.points(bob.address)).to.equal(0)

    await expect(pickem.settle(101, 2)).to.be.revertedWithCustomError(pickem, 'NotOpen')
  })

  it('cancelled matches take no more picks', async () => {
    const { pickem, alice } = await setup()
    await pickem.cancel(102)
    await expect(pickem.connect(alice).pick(102, 1)).to.be.revertedWithCustomError(pickem, 'NotOpen')
  })

  it('ownership can be handed over', async () => {
    const { pickem, alice } = await setup()
    await pickem.transferOwnership(alice.address)
    expect(await pickem.owner()).to.equal(alice.address)
    await pickem.connect(alice).settle(101, 1)
  })
})
