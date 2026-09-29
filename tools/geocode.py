"""
Geocodes tournament venues and team home cities via OpenStreetMap Nominatim -> seed/geo.json.
Nominatim policy: max 1 request/second, identifying User-Agent.
    python tools/geocode.py
"""
import json
import os
import time
import urllib.parse
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
UA = "vct-tournament-student-project/1.0 (KNU)"

# Home cities of organisations, where known. Everything else falls back to the country.
TEAM_CITY = {
    "100 Thieves": "Los Angeles", "Cloud9": "Los Angeles", "Sentinels": "Los Angeles", "NRG": "Los Angeles",
    "Evil Geniuses": "Los Angeles", "G2 Esports": "Los Angeles", "ENVY": "Dallas", "2GAME eSports": "São Paulo",
    "LOUD": "São Paulo", "FURIA": "São Paulo", "MIBR": "Rio de Janeiro", "LEVIATÁN": "Santiago",
    "KRÜ Esports": "Buenos Aires",
    "FNATIC": "London", "Team Heretics": "Madrid", "Team Liquid": "Utrecht", "Karmine Corp": "Paris",
    "Team Vitality": "Paris", "BBL Esports": "Istanbul", "FUT Esports": "Istanbul", "Natus Vincere": "Kyiv",
    "GIANTX": "Madrid", "KOI": "Barcelona", "Gentle Mates": "Paris", "Eintracht Frankfurt": "Frankfurt",
    "Paper Rex": "Singapore", "DRX": "Seoul", "T1": "Seoul", "Gen.G": "Seoul", "Nongshim RedForce": "Seoul",
    "ZETA DIVISION": "Tokyo", "DetonatioN FocusMe": "Tokyo", "Rex Regum Qeon": "Jakarta", "TALON": "Bangkok",
    "FULL SENSE": "Bangkok", "Global Esports": "Mumbai", "Team Secret": "Manila", "BOOM Esports": "Jakarta",
    "EDward Gaming": "Shanghai", "FunPlus Phoenix": "Shanghai", "Bilibili Gaming": "Shanghai",
    "Trace Esports": "Shanghai", "JDG Esports": "Beijing", "TYLOO": "Shanghai", "Wolves Esports": "Shanghai",
    "Dragon Ranger Gaming": "Shanghai", "All Gamers": "Shanghai", "Nova Esports": "Shanghai",
    "Titan Esports Club": "Shanghai", "Xi Lai Gaming": "Chengdu",
    # names as vlr.gg spells them in 2026
    "KIWOOM DRX": "Seoul", "Guangzhou Huadu Bilibili Gaming": "Guangzhou", "JD Mall JDG Esports": "Beijing",
    "Wuxi Titan Esports Club": "Wuxi", "Eternal Fire": "Istanbul", "ULF Esports": "Istanbul",
}

_last = 0.0


def geocode(q):
    global _last
    wait = 1.1 - (time.time() - _last)
    if wait > 0:
        time.sleep(wait)
    url = "https://nominatim.openstreetmap.org/search?" + urllib.parse.urlencode({"q": q, "format": "json", "limit": 1})
    req = urllib.request.Request(url, headers={"User-Agent": UA})
    with urllib.request.urlopen(req, timeout=30) as r:
        _last = time.time()
        res = json.loads(r.read())
    return (float(res[0]["lat"]), float(res[0]["lon"])) if res else None


def main():
    data = json.load(open(os.path.join(ROOT, "seed", "vct2026.json"), encoding="utf-8"))
    out = {"venues": {}, "teams": {}}
    for ev in data["events"]:
        loc = ev["location"]
        if loc and loc not in out["venues"]:
            # "Suhai Music Hall, São Paulo" -> try the full string, then just the city
            hit = geocode(loc) or geocode(loc.split(",")[-1].strip()) or geocode(loc.split("&")[0].strip())
            out["venues"][loc] = {"lat": hit[0], "lng": hit[1]} if hit else None
            print("venue", loc, hit)
    cache = {}
    for t in data["teams"]:
        city = TEAM_CITY.get(t["name"])
        q = f"{city}" if city else t.get("country") or ""
        if q not in cache:
            cache[q] = geocode(q) if q else None
        hit = cache[q]
        out["teams"][str(t["vlrId"])] = {"city": city or t.get("country"), "lat": hit[0], "lng": hit[1]} if hit else None
        print("team", t["name"], q, hit)
    json.dump(out, open(os.path.join(ROOT, "seed", "geo.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=1)


if __name__ == "__main__":
    main()
