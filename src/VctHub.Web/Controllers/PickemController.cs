using Microsoft.AspNetCore.Mvc;

namespace VctHub.Web.Controllers;

/// <summary>C20: Pick'em on an Ethereum smart contract (Solidity, see /contracts), UI with web3.js.</summary>
public class PickemController(IConfiguration config) : Controller
{
    public record ChainConfig(string? Address, long ChainId, string ChainName, string Rpc, string? Explorer);

    [HttpGet("/pickem")]
    public IActionResult Index()
    {
        ViewData["Title"] = "Pick'em";
        ViewData["Description"] = "Pick VCT winners on the blockchain. Testnet only, no real money.";
        var chainId = long.TryParse(config["PICKEM_CHAIN_ID"], out var id) ? id : 31337;
        return View(new ChainConfig(
            config["PICKEM_ADDRESS"],
            chainId,
            chainId == 11155111 ? "Sepolia" : "Hardhat local",
            config["PICKEM_RPC"] ?? (chainId == 11155111 ? "https://ethereum-sepolia-rpc.publicnode.com" : "http://127.0.0.1:8545"),
            chainId == 11155111 ? "https://sepolia.etherscan.io" : null));
    }
}
