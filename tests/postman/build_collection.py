"""
Builds vct-api.postman_collection.json (easier to keep tests readable here than in raw collection JSON).
    python tests/postman/build_collection.py
    npx newman run tests/postman/vct-api.postman_collection.json -e tests/postman/local.postman_environment.json
"""
import json
import os

HERE = os.path.dirname(os.path.abspath(__file__))
NUMERIC_VARS = ["regionId", "teamId", "tournamentId"]


def req(name, method, path, tests, body=None, pre=None):
    url = {"raw": "{{baseUrl}}" + path, "host": ["{{baseUrl}}"],
           "path": [p for p in path.split("?")[0].split("/") if p]}
    if "?" in path:
        url["query"] = [{"key": kv.split("=")[0], "value": kv.split("=")[1]} for kv in path.split("?")[1].split("&")]
    r = {"name": name,
         "request": {"method": method, "header": [{"key": "Content-Type", "value": "application/json"}], "url": url},
         "event": [{"listen": "test", "script": {"type": "text/javascript", "exec": tests.strip().split("\n")}}]}
    if body is not None:
        r["request"]["body"] = {"mode": "raw", "raw": body if isinstance(body, str) else json.dumps(body, indent=2)}
    if pre:
        r["event"].append({"listen": "prerequest", "script": {"type": "text/javascript", "exec": pre.strip().split("\n")}})
    return r


OK = 'pm.test("200 OK", () => pm.response.to.have.status(200));'

READ = [
    req("Regions list", "GET", "/api/regions", OK + """
pm.test("five regions incl. EMEA", () => {
  const r = pm.response.json();
  pm.expect(r).to.have.lengthOf(5);
  pm.expect(r.map(x => x.code)).to.include("emea");
  pm.collectionVariables.set("regionId", r.find(x => x.code === "emea").id);
});"""),
    req("Same read again is served from cache", "GET", "/api/regions", OK + """
pm.test("X-Cache: HIT", () => pm.expect(pm.response.headers.get("X-Cache")).to.eql("HIT"));"""),
    req("Teams page 1 (limit 5)", "GET", "/api/teams?limit=5", OK + """
const p = pm.response.json();
pm.test("page shape", () => {
  pm.expect(p).to.have.all.keys("value", "count", "skip", "limit", "nextLink");
  pm.expect(p.value).to.have.lengthOf(5);
  pm.expect(p.skip).to.eql(0);
});
pm.test("nextLink points at skip=5", () => pm.expect(p.nextLink).to.include("skip=5"));
pm.collectionVariables.set("nextLink", p.nextLink);
pm.collectionVariables.set("firstTeam", p.value[0].name);"""),
    req("Teams page 2 via nextLink", "GET", "/api/teams", OK + """
pm.test("second page starts after the first", () => {
  const p = pm.response.json();
  pm.expect(p.skip).to.eql(5);
  pm.expect(p.value[0].name).to.not.eql(pm.collectionVariables.get("firstTeam"));
});""", pre='pm.request.url = pm.collectionVariables.get("nextLink");'),
    req("Last page has no nextLink", "GET", "/api/teams?skip=1000&limit=10", OK + """
pm.test("empty, nextLink null", () => {
  const p = pm.response.json();
  pm.expect(p.value).to.be.empty;
  pm.expect(p.nextLink).to.be.null;
});"""),
    req("Limit is capped at 100", "GET", "/api/players?limit=5000", OK + """
pm.test("limit clamped", () => pm.expect(pm.response.json().limit).to.eql(100));"""),
    req("Players sorted by ACS", "GET", "/api/players?sort=acs&limit=10", OK + """
pm.test("descending ACS", () => {
  const acs = pm.response.json().value.map(p => p.acs);
  pm.expect(acs).to.eql([...acs].sort((a, b) => b - a));
});"""),
    req("Tournaments list", "GET", "/api/tournaments?limit=3", OK + """
const t = pm.response.json().value[0];
pm.test("has ISO dates", () => pm.expect(t.startDate).to.match(/^\\d{4}-\\d{2}-\\d{2}$/));
pm.collectionVariables.set("tournamentId", t.id);"""),
    req("Tournament matches", "GET", "/api/tournaments/{{tournamentId}}/matches?limit=5", OK + """
pm.test("all belong to the tournament", () => {
  const id = Number(pm.collectionVariables.get("tournamentId"));
  pm.response.json().value.forEach(m => pm.expect(m.tournamentId).to.eql(id));
});"""),
    req("Upcoming matches are ascending", "GET", "/api/matches?status=Upcoming&limit=20", OK + """
pm.test("sorted by date asc", () => {
  const d = pm.response.json().value.map(m => m.scheduledAt);
  pm.expect(d).to.eql([...d].sort());
});"""),
    req("Search tolerates typos", "GET", "/api/search?q=fnatik&limit=5", OK + """
pm.test("FNATIC is the top hit", () => pm.expect(pm.response.json().hits[0].title).to.eql("FNATIC"));
pm.test("engine reported", () => pm.expect(pm.response.headers.get("X-Search-Engine")).to.be.oneOf(["elasticsearch", "postgres"]));"""),
    req("Search filter by type", "GET", "/api/search?q=champions&type=event", OK + """
pm.test("only events", () => pm.response.json().hits.forEach(h => pm.expect(h.type).to.eql("event")));"""),
    req("Search needs 2+ chars -> 400", "GET", "/api/search?q=x", 'pm.test("400", () => pm.response.to.have.status(400));'),
    req("Unknown team -> 404", "GET", "/api/teams/999999", """
pm.test("404", () => pm.response.to.have.status(404));
pm.test("problem+json", () => pm.expect(pm.response.headers.get("Content-Type")).to.include("problem+json"));"""),
]

MATCH = {"tournamentId": "{{tournamentId}}", "teamAId": "{{teamId}}", "teamBId": 1,
         "scheduledAt": "2026-10-10T15:00:00Z", "status": "Upcoming", "bestOf": 3}

WRITE = [
    req("Create team", "POST", "/api/teams", """
pm.test("201 Created", () => pm.response.to.have.status(201));
pm.test("Location header", () => pm.response.to.have.header("Location"));
const t = pm.response.json();
pm.test("slug generated", () => pm.expect(t.slug).to.eql("postman-test-team"));
pm.collectionVariables.set("teamId", t.id);""",
        body={"name": "Postman Test Team", "tag": "PTT", "regionId": "{{regionId}}", "country": "Ukraine",
              "city": "Kyiv", "latitude": 50.45, "longitude": 30.52}),
    req("Duplicate team -> 409", "POST", "/api/teams", 'pm.test("409 Conflict", () => pm.response.to.have.status(409));',
        body={"name": "Postman Test Team", "tag": "PTT", "regionId": "{{regionId}}"}),
    req("Invalid team -> 400", "POST", "/api/teams", """
pm.test("400 Bad Request", () => pm.response.to.have.status(400));
pm.test("field errors", () => {
  const e = pm.response.json().errors;
  pm.expect(e).to.have.property("Name");
  pm.expect(e).to.have.property("Tag");
});""", body={"tag": "WAY-TOO-LONG-TAG", "regionId": 1}),
    req("Malformed JSON -> 400", "POST", "/api/teams", 'pm.test("400 Bad Request", () => pm.response.to.have.status(400));',
        body="{ not json"),
    req("Unknown region -> 400", "POST", "/api/teams", """
pm.test("400 Bad Request", () => pm.response.to.have.status(400));
pm.test("regionId error", () => pm.expect(pm.response.json().errors).to.have.property("regionId"));""",
        body={"name": "Nowhere FC", "tag": "NWH", "regionId": 424242}),
    req("Write evicted the cache", "GET", "/api/regions", OK + """
pm.test("X-Cache: MISS after a write", () => pm.expect(pm.response.headers.get("X-Cache")).to.eql("MISS"));"""),
    req("Get created team", "GET", "/api/teams/{{teamId}}", OK + """
pm.test("same data", () => pm.expect(pm.response.json().city).to.eql("Kyiv"));"""),
    req("Update team", "PUT", "/api/teams/{{teamId}}", OK + """
pm.test("city changed", () => pm.expect(pm.response.json().city).to.eql("Lviv"));""",
        body={"name": "Postman Test Team", "tag": "PTT", "regionId": "{{regionId}}", "city": "Lviv"}),
    req("Create player in team", "POST", "/api/players", """
pm.test("201 Created", () => pm.response.to.have.status(201));
const p = pm.response.json();
pm.test("linked to team", () => pm.expect(p.teamId).to.eql(Number(pm.collectionVariables.get("teamId"))));
pm.collectionVariables.set("playerId", p.id);""",
        body={"nickname": "postmanPlayer", "realName": "Test Testenko", "role": "Duelist", "teamId": "{{teamId}}"}),
    req("Team roster has the player", "GET", "/api/teams/{{teamId}}/players", OK + """
pm.test("one player", () => pm.expect(pm.response.json().map(p => p.nickname)).to.eql(["postmanPlayer"]));"""),
    req("Player with unknown team -> 400", "POST", "/api/players",
        'pm.test("400 Bad Request", () => pm.response.to.have.status(400));', body={"nickname": "ghost", "teamId": 999999}),
    req("Match vs itself -> 400", "POST", "/api/matches",
        'pm.test("400 Bad Request", () => pm.response.to.have.status(400));', body={**MATCH, "teamBId": "{{teamId}}"}),
    req("Impossible score -> 400", "POST", "/api/matches",
        'pm.test("400 Bad Request", () => pm.response.to.have.status(400));',
        body={**MATCH, "status": "Completed", "scoreA": 3, "scoreB": 1}),
    req("Create match", "POST", "/api/matches", """
pm.test("201 Created", () => pm.response.to.have.status(201));
const m = pm.response.json();
pm.test("teams embedded", () => pm.expect(m.teamA.name).to.eql("Postman Test Team"));
pm.collectionVariables.set("matchId", m.id);""", body={**MATCH, "series": "Showmatch"}),
    req("Filter matches by team", "GET", "/api/matches?teamId={{teamId}}", OK + """
pm.test("exactly our match", () => pm.expect(pm.response.json().count).to.eql(1));"""),
    req("Delete team with matches -> 409", "DELETE", "/api/teams/{{teamId}}",
        'pm.test("409 Conflict", () => pm.response.to.have.status(409));'),
    req("Finish match", "PUT", "/api/matches/{{matchId}}", OK + """
pm.test("completed 2:1", () => {
  const m = pm.response.json();
  pm.expect(m.status).to.eql("Completed");
  pm.expect([m.scoreA, m.scoreB]).to.eql([2, 1]);
});""", body={**MATCH, "status": "Completed", "scoreA": 2, "scoreB": 1}),
]

GONE = 'pm.test("204 No Content", () => pm.response.to.have.status(204));'
CLEANUP = [
    req("Delete match", "DELETE", "/api/matches/{{matchId}}", GONE),
    req("Delete player", "DELETE", "/api/players/{{playerId}}", GONE),
    req("Delete team", "DELETE", "/api/teams/{{teamId}}", GONE),
    req("Deleted team -> 404", "GET", "/api/teams/{{teamId}}", 'pm.test("404", () => pm.response.to.have.status(404));'),
    req("Delete again -> 404", "DELETE", "/api/teams/{{teamId}}", 'pm.test("404", () => pm.response.to.have.status(404));'),
]

collection = {
    "info": {"name": "VCT Hub API", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json",
             "description": "CRUD + paging tests for the VCT Hub REST API (C5, B1, QA1)."},
    "item": [{"name": "Read", "item": READ}, {"name": "Write", "item": WRITE}, {"name": "Cleanup", "item": CLEANUP}],
    "event": [{"listen": "test", "script": {"type": "text/javascript", "exec": [
        "pm.test('responds under 2s', () => pm.expect(pm.response.responseTime).to.be.below(2000));"]}}],
    "variable": [{"key": k, "value": ""} for k in ["regionId", "teamId", "playerId", "matchId", "tournamentId", "nextLink", "firstTeam"]],
}

text = json.dumps(collection, indent=2, ensure_ascii=False)
for v in NUMERIC_VARS:  # ids go into bodies as numbers, not strings
    text = text.replace(f'\\"{{{{{v}}}}}\\"', f"{{{{{v}}}}}")
with open(os.path.join(HERE, "vct-api.postman_collection.json"), "w", encoding="utf-8") as f:
    f.write(text)

for name, url in [("local", "http://localhost:8080"), ("dev", "http://localhost:5175")]:
    with open(os.path.join(HERE, f"{name}.postman_environment.json"), "w") as f:
        json.dump({"name": name, "values": [{"key": "baseUrl", "value": url, "enabled": True}]}, f, indent=2)
print("collection written")
