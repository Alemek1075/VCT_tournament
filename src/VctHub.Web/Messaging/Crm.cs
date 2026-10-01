using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace VctHub.Web.Messaging;

/// <summary>New account on the site (password or Google).</summary>
public record UserSignedUp(string Email, string Name, string Workspace, string Source, DateTime At);

/// <summary>Someone asked for the Team plan on the Premium page.</summary>
public record PlanLead(string Email, string Name, string Workspace, string Company, int Seats, string Message, DateTime At);

/// <summary>
/// B3: HubSpot CRM over its REST API (private app token in HUBSPOT_TOKEN).
/// Contacts are upserted by email, so a repeated event never creates a duplicate.
/// </summary>
public class HubSpotClient(HttpClient http, IConfiguration config, ILogger<HubSpotClient> log)
{
    const string Base = "https://api.hubapi.com";
    public bool Enabled => !string.IsNullOrWhiteSpace(config["HUBSPOT_TOKEN"]);

    public async Task UpsertContactAsync(string email, Dictionary<string, string?> props, CancellationToken ct = default)
    {
        props["email"] = email;
        var body = new { properties = props.Where(p => !string.IsNullOrWhiteSpace(p.Value)).ToDictionary() };

        // try create first; 409 means the email exists, then update that contact by email
        using var created = await SendAsync(HttpMethod.Post, "/crm/v3/objects/contacts", body, ct);
        if (created.StatusCode == HttpStatusCode.Conflict)
        {
            // an existing contact keeps its stage: HubSpot won't move a lead back to subscriber
            if (props.GetValueOrDefault("lifecyclestage") == "subscriber") body.properties.Remove("lifecyclestage");
            using var updated = await SendAsync(HttpMethod.Patch, $"/crm/v3/objects/contacts/{Uri.EscapeDataString(email)}?idProperty=email", body, ct);
            await EnsureOk(updated, "update", ct);
            if (updated.IsSuccessStatusCode) log.LogInformation("HubSpot: updated contact {Email}", email);
            return;
        }
        await EnsureOk(created, "create", ct);
        if (created.IsSuccessStatusCode) log.LogInformation("HubSpot: created contact {Email}", email);
    }

    Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object body, CancellationToken ct)
    {
        var req = new HttpRequestMessage(method, Base + path) { Content = JsonContent.Create(body) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config["HUBSPOT_TOKEN"]);
        return http.SendAsync(req, ct);
    }

    async Task EnsureOk(HttpResponseMessage res, string what, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode) return;
        var text = await res.Content.ReadAsStringAsync(ct);
        // 400 = HubSpot rejects the data itself (e.g. an invalid email); retrying won't help, so log and drop
        if (res.StatusCode == HttpStatusCode.BadRequest)
        {
            log.LogWarning("HubSpot {What} rejected: {Body}", what, text[..Math.Min(text.Length, 300)]);
            return;
        }
        throw new HttpRequestException($"HubSpot {what} failed: {(int)res.StatusCode} {text[..Math.Min(text.Length, 300)]}", null, res.StatusCode);
    }
}

/// <summary>Takes "crm.*" events off the queue (B8) and pushes them to HubSpot, so sign-up never waits for the CRM.</summary>
public class CrmWorker(IMessageBus bus, HubSpotClient hubspot, ILogger<CrmWorker> log) : BackgroundService
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!hubspot.Enabled) { log.LogInformation("HUBSPOT_TOKEN not set, CRM sync is off"); return; }
        await bus.SubscribeAsync("crm-sync", "crm.#", Handle, ct);
    }

    async Task Handle(string key, string body)
    {
        switch (key)
        {
            case "crm.signup":
                var s = JsonSerializer.Deserialize<UserSignedUp>(body, Json)!;
                await hubspot.UpsertContactAsync(s.Email, new()
                {
                    ["firstname"] = s.Name,
                    ["company"] = s.Workspace,
                    ["lifecyclestage"] = "subscriber",
                    ["hs_lead_status"] = "NEW",
                });
                break;
            case "crm.lead":
                var l = JsonSerializer.Deserialize<PlanLead>(body, Json)!;
                await hubspot.UpsertContactAsync(l.Email, new()
                {
                    ["firstname"] = l.Name,
                    ["company"] = string.IsNullOrWhiteSpace(l.Company) ? l.Workspace : l.Company,
                    ["lifecyclestage"] = "lead",
                    ["hs_lead_status"] = "OPEN",
                    ["message"] = $"Team plan request: {l.Seats} seats. {l.Message}",
                });
                break;
            default:
                log.LogWarning("Unknown CRM event {Key}", key);
                break;
        }
    }
}
