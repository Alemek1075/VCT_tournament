"""
Pulls VCT 2026 data from vlr.gg into seed/vct2026.json (+ images into seed/img).

Run once, the result is committed. Be polite: one request per ~0.7s.
    python tools/scrape_vlr.py
"""
import html
import json
import os
import re
import sys
import time
import urllib.request

BASE = "https://www.vlr.gg"
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "seed")
IMG = os.path.join(OUT, "img")
UA = "Mozilla/5.0 (KNU student project, non-commercial)"

EVENTS = [
    (2682, "americas", "Kickoff"), (2683, "pacific", "Kickoff"), (2684, "emea", "Kickoff"), (2685, "china", "Kickoff"),
    (2760, "international", "Masters"),
    (2860, "americas", "Stage 1"), (2775, "pacific", "Stage 1"), (2863, "emea", "Stage 1"), (2864, "china", "Stage 1"),
    (2765, "international", "Masters"),
    (2977, "americas", "Stage 2"), (2776, "pacific", "Stage 2"), (2976, "emea", "Stage 2"), (2978, "china", "Stage 2"),
    (2766, "international", "Champions"),
]

AGENT_ROLE = {
    "jett": "Duelist", "raze": "Duelist", "phoenix": "Duelist", "reyna": "Duelist", "yoru": "Duelist",
    "neon": "Duelist", "iso": "Duelist", "waylay": "Duelist",
    "sova": "Initiator", "skye": "Initiator", "breach": "Initiator", "kayo": "Initiator", "fade": "Initiator",
    "gekko": "Initiator", "tejo": "Initiator",
    "omen": "Controller", "brimstone": "Controller", "viper": "Controller", "astra": "Controller",
    "harbor": "Controller", "clove": "Controller",
    "sage": "Sentinel", "cypher": "Sentinel", "killjoy": "Sentinel", "chamber": "Sentinel", "deadlock": "Sentinel",
    "vyse": "Sentinel", "veto": "Sentinel",
}

_last = 0.0


CACHE = os.path.join(os.environ.get("TEMP", "/tmp"), "vlr-cache")


def get(path):
    global _last
    os.makedirs(CACHE, exist_ok=True)
    key = os.path.join(CACHE, re.sub(r"\W+", "_", path) + ".html")
    if os.path.exists(key):
        return open(key, encoding="utf-8").read()
    wait = 0.7 - (time.time() - _last)
    if wait > 0:
        time.sleep(wait)
    url = path if path.startswith("http") else BASE + path
    req = urllib.request.Request(url, headers={"User-Agent": UA})
    with urllib.request.urlopen(req, timeout=30) as r:
        _last = time.time()
        body = r.read().decode("utf-8", "replace")
    with open(key, "w", encoding="utf-8") as f:
        f.write(body)
    return body


def text(s):
    return html.unescape(re.sub(r"<[^>]+>", "", s)).strip()


def download(src, name):
    # vlr uses local placeholder silhouettes ("/img/base/ph/sil.png") for players without a photo
    if not src or not src.startswith(("//", "http")) or "/img/vlr/tmp/" in src:
        return None
    if src.startswith("//"):
        src = "https:" + src
    ext = os.path.splitext(src.split("?")[0])[1] or ".png"
    fname = f"{name}{ext}"
    path = os.path.join(IMG, fname)
    if not os.path.exists(path):
        req = urllib.request.Request(src, headers={"User-Agent": UA})
        try:
            with urllib.request.urlopen(req, timeout=30) as r, open(path, "wb") as f:
                f.write(r.read())
        except Exception as e:  # a missing photo is not worth failing the run
            print("  img fail", src, e, file=sys.stderr)
            return None
    return fname


def parse_event(eid, region, stage):
    page = get(f"/event/{eid}")
    title = text(re.search(r'event-header-main-title">(.*?)</h1>', page, re.S).group(1))
    logo = re.search(r'event-header-thumb">.*?<img src="([^"]+)"', page, re.S).group(1)
    meta = dict(
        (text(k).lower(), text(v))
        for k, v in re.findall(r'class="ge-text-light label">(.*?)</div>\s*<div class="value"[^>]*>(.*?)</div>', page, re.S)
    )
    teams = []
    for block in re.findall(r'<div class="wf-card event-team">(.*?)<div class="event-team-note', page, re.S):
        m = re.search(r'href="/team/(\d+)/[^"]*">(.*?)</a>', block, re.S)
        if not m:
            continue
        players = [
            {"vlrId": int(pid), "nick": text(nick), "country": cc}
            for pid, cc, nick in re.findall(
                r'href="/player/(\d+)/[^"]*"[^>]*>\s*<i class="flag mod-(\w+)"[^>]*></i>(.*?)</a>', block, re.S)
        ]
        teams.append({"vlrId": int(m.group(1)), "name": text(m.group(2)), "players": players})
    return {
        "vlrId": eid, "name": title, "region": region, "stage": stage,
        "dates": meta.get("dates", ""), "prize": meta.get("prize", ""), "location": meta.get("location", ""),
        "logo": download(logo, f"event-{eid}"), "teams": teams,
    }


def parse_matches(eid):
    page = get(f"/event/matches/{eid}/x/?series_id=all")
    out = []
    day = None
    for chunk in re.split(r'(<div class="wf-label mod-large">.*?</div>)', page, flags=re.S):
        if 'wf-label mod-large' in chunk:
            day = text(re.sub(r'<span.*?</span>', '', chunk, flags=re.S))
            continue
        for mid, body in re.findall(r'<a href="/(\d+)/[^"]*" class="wf-module-item match-item[^"]*">(.*?)</a>', chunk, re.S):
            names = [text(n) for n in re.findall(r'<div class="text-of">\s*<span class="flag[^"]*"></span>(.*?)</div>', body, re.S)]
            scores = [text(s) for s in re.findall(r'match-item-vs-team-score[^"]*">(.*?)</div>', body, re.S)]
            status = re.search(r'ml-status">(.*?)</div>', body)
            series = re.search(r'match-item-event-series[^"]*">(.*?)</div>', body, re.S)
            tm = re.search(r'match-item-time">(.*?)</div>', body, re.S)
            if len(names) != 2:
                continue
            out.append({
                "vlrId": int(mid), "day": day, "time": text(tm.group(1)) if tm else "",
                "teamA": names[0], "teamB": names[1],
                "scoreA": scores[0] if scores else "", "scoreB": scores[1] if len(scores) > 1 else "",
                "status": text(status.group(1)) if status else "", "series": text(series.group(1)) if series else "",
            })
    return out


def parse_stats(eid):
    page = get(f"/event/stats/{eid}/x/")
    body = re.search(r"<tbody>(.*?)</tbody>", page, re.S)
    rows = []
    for tr in re.findall(r"<tr>(.*?)</tr>", body.group(1) if body else "", re.S):
        pid = re.search(r'href="/player/(\d+)/', tr)
        if not pid:
            continue
        agents = re.findall(r"agents/(\w+)\.png", tr)
        cols = dict(re.findall(r'data-col="(\w+)"[^>]*>(.*?)</td>', tr, re.S))
        rows.append({
            "vlrId": int(pid.group(1)), "agents": agents,
            **{k: text(v) for k, v in cols.items() if k in (
                "maps", "rnd", "rating2", "acs", "kd", "kast", "adr", "kpr", "apr", "hsp", "fk", "fd", "k", "d", "a")},
        })
    return rows


def parse_team(tid):
    page = get(f"/team/{tid}/x")
    logo = re.search(r'team-header-logo">\s*<div>\s*<img src="([^"]+)"', page, re.S)
    tag = re.search(r'team-header-tag">(.*?)</h2>', page, re.S)
    links = re.search(r'team-header-links">(.*?)</div>', page, re.S)
    hrefs = [h for h in re.findall(r'href="([^"]+)"', links.group(1))] if links else []
    country = re.search(r'team-header-country">\s*<i class="flag mod-(\w+)"></i>(.*?)</div>', page, re.S)
    roster = []
    for pid, img, alias, real, role in re.findall(
            r'team-roster-item">\s*<a href="/player/(\d+)/[^"]*"[^>]*>\s*<div class="team-roster-item-img">\s*<img src="([^"]+)">'
            r'.*?team-roster-item-name-alias">(.*?)</div>\s*<div class="team-roster-item-name-real">(.*?)</div>(.*?)</a>',
            page, re.S):
        r = re.search(r'name-role">(.*?)</div>', role, re.S)
        roster.append({"vlrId": int(pid), "nick": text(alias), "real": text(real),
                       "staff": text(r.group(1)) if r else None,
                       "captain": "Team Captain" in alias, "img": img})
    return {
        "vlrId": tid,
        "tag": text(tag.group(1)) if tag else "",
        "logo": download(logo.group(1), f"team-{tid}") if logo else None,
        "website": next((h for h in hrefs if h and "x.com" not in h and "twitter" not in h), None),
        "twitter": next((h for h in hrefs if "x.com" in h or "twitter" in h), None),
        "countryCode": country.group(1) if country else None,
        "country": text(country.group(2)) if country else None,
        "roster": roster,
    }


def main():
    os.makedirs(IMG, exist_ok=True)
    events, teams, stats = [], {}, {}
    for eid, region, stage in EVENTS:
        print("event", eid)
        ev = parse_event(eid, region, stage)
        ev["matches"] = parse_matches(eid)
        for row in parse_stats(eid):
            stats.setdefault(row["vlrId"], []).append({"event": eid, **row})
        for t in ev["teams"]:
            if t["vlrId"] not in teams:
                teams[t["vlrId"]] = {"name": t["name"], "region": region}
            if region != "international":
                teams[t["vlrId"]]["region"] = region
        events.append(ev)

    for tid, t in teams.items():
        print("team", t["name"])
        t.update(parse_team(tid))
        for p in t["roster"]:
            p["photo"] = download(p.pop("img"), f"player-{p['vlrId']}") if not p["staff"] else None
            p.pop("img", None)
            rows = stats.get(p["vlrId"], [])
            agents = [a for r in rows for a in r["agents"][:1]]
            if agents:
                main_agent = max(set(agents), key=agents.count)
                p["mainAgent"] = main_agent
                p["role"] = AGENT_ROLE.get(main_agent, "Flex")
            p["stats"] = rows

    with open(os.path.join(OUT, "vct2026.json"), "w", encoding="utf-8") as f:
        json.dump({"source": "vlr.gg", "scrapedAt": time.strftime("%Y-%m-%d"),
                   "events": events, "teams": [{"vlrId": k, **v} for k, v in teams.items()]},
                  f, ensure_ascii=False, indent=1)
    print("done:", len(events), "events,", len(teams), "teams")


if __name__ == "__main__":
    main()
