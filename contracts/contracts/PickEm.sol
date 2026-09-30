// SPDX-License-Identifier: MIT
pragma solidity ^0.8.24;

/// @title VCT Hub Pick'em (C20)
/// @notice Fans pick the winner of VCT matches before they start. The owner opens matches and
///         posts results; every correct pick is one point. No money involved, only reputation.
contract PickEm {
    enum Status { None, Open, Settled, Cancelled }

    struct Game {
        uint64 lockTime;   // picks close at this unix time (match start)
        uint8 winner;      // 1 = team A, 2 = team B, 0 = not settled yet
        Status status;
        uint32 picksA;
        uint32 picksB;
    }

    address public owner;
    mapping(uint256 => Game) public games;                       // site match id => game
    mapping(uint256 => mapping(address => uint8)) public pickOf;  // match id => fan => 1 or 2
    mapping(address => uint256) public points;
    mapping(uint256 => address[]) private pickers;
    uint256[] public gameIds;

    event MatchOpened(uint256 indexed matchId, uint64 lockTime);
    event Picked(uint256 indexed matchId, address indexed fan, uint8 team);
    event MatchSettled(uint256 indexed matchId, uint8 winner);
    event MatchCancelled(uint256 indexed matchId);
    event OwnershipTransferred(address indexed from, address indexed to);

    error NotOwner();
    error BadTeam();
    error NotOpen();
    error Locked();
    error AlreadyPicked();
    error AlreadyExists();

    modifier onlyOwner() {
        if (msg.sender != owner) revert NotOwner();
        _;
    }

    constructor() {
        owner = msg.sender;
    }

    function transferOwnership(address to) external onlyOwner {
        emit OwnershipTransferred(owner, to);
        owner = to;
    }

    /// @notice Open several matches at once (the sync script sends the site's upcoming matches).
    function openMatches(uint256[] calldata ids, uint64[] calldata lockTimes) external onlyOwner {
        require(ids.length == lockTimes.length, "length mismatch");
        for (uint256 i = 0; i < ids.length; i++) {
            if (games[ids[i]].status != Status.None) continue; // already opened, skip quietly
            games[ids[i]] = Game(lockTimes[i], 0, Status.Open, 0, 0);
            gameIds.push(ids[i]);
            emit MatchOpened(ids[i], lockTimes[i]);
        }
    }

    function pick(uint256 matchId, uint8 team) external {
        Game storage g = games[matchId];
        if (g.status != Status.Open) revert NotOpen();
        if (block.timestamp >= g.lockTime) revert Locked();
        if (team != 1 && team != 2) revert BadTeam();
        if (pickOf[matchId][msg.sender] != 0) revert AlreadyPicked();

        pickOf[matchId][msg.sender] = team;
        pickers[matchId].push(msg.sender);
        if (team == 1) g.picksA++; else g.picksB++;
        emit Picked(matchId, msg.sender, team);
    }

    /// @notice Post the result and give a point to everyone who picked the winner.
    function settle(uint256 matchId, uint8 winner) external onlyOwner {
        Game storage g = games[matchId];
        if (g.status != Status.Open) revert NotOpen();
        if (winner != 1 && winner != 2) revert BadTeam();

        g.status = Status.Settled;
        g.winner = winner;
        address[] storage list = pickers[matchId];
        for (uint256 i = 0; i < list.length; i++) {
            if (pickOf[matchId][list[i]] == winner) points[list[i]]++;
        }
        emit MatchSettled(matchId, winner);
    }

    function cancel(uint256 matchId) external onlyOwner {
        if (games[matchId].status != Status.Open) revert NotOpen();
        games[matchId].status = Status.Cancelled;
        emit MatchCancelled(matchId);
    }

    function gameCount() external view returns (uint256) {
        return gameIds.length;
    }

    function pickersOf(uint256 matchId) external view returns (address[] memory) {
        return pickers[matchId];
    }
}
