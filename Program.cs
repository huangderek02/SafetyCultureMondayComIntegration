using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Collections.Generic;
using System.Threading.Tasks;

class Program
{
    // ── CONFIG ─────────────────────────────────────────────────────────────
    private const string EnvTokenVar = "SAFETYCULTURE_API_TOKEN";
    private const string FallbackToken = "9fe734dc1d6ceb4402ac97cf0a053f5c04966f9fe11b4068725bed0a4b09d851";
    private const string TemplateId = "template_93bca68053f4450e82f78071d176b5c1";
    private const string BaseUrl = "https://api.safetyculture.io";

    // Custom labels to extract
    static readonly HashSet<string> DesiredLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "Part-Number", "Transaction Type", "Quantity"
    };

    static async Task Main()
    {
        // 0) Load API token
        var apiToken = Environment.GetEnvironmentVariable(EnvTokenVar);
        if (string.IsNullOrWhiteSpace(apiToken))
        {
            Console.WriteLine($"WARNING: Env var '{EnvTokenVar}' not found; using fallback token.");
            apiToken = FallbackToken;
        }

        using var client = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);

        // Compute start of today (UTC) for update filter
        DateTimeOffset todayStart = DateTimeOffset.UtcNow.Date;
        Console.WriteLine($"Filtering inspections updated since: {todayStart:O}\n");

        // 1) Fetch all audits for the template
        string searchUrl = $"/audits/search?template={TemplateId}";
        Console.WriteLine($"[DEBUG] GET {searchUrl}");
        var resp = await client.GetAsync(searchUrl);
        Console.WriteLine($"[DEBUG] Response: {(int)resp.StatusCode} {resp.ReasonPhrase}");
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            Console.Error.WriteLine("ERROR: Unauthorized (401). Check your API token.");
            return;
        }
        resp.EnsureSuccessStatusCode();

        using var listDoc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var audits = listDoc.RootElement.GetProperty("audits");

        bool any = false;
        // 2) Process each audit and filter by modified_at
        foreach (var a in audits.EnumerateArray())
        {
            if (!a.TryGetProperty("audit_id", out var aid))
                continue;
            string auditId = aid.GetString()!;

            // Fetch full details
            var detailResp = await client.GetAsync($"/audits/{auditId}");
            if (!detailResp.IsSuccessStatusCode)
                continue;

            using var detDoc = JsonDocument.Parse(await detailResp.Content.ReadAsStringAsync());
            var root = detDoc.RootElement;

            // Check modified_at
            if (!root.TryGetProperty("modified_at", out var modProp) ||
                !DateTimeOffset.TryParse(modProp.GetString(), out var modifiedAt) ||
                modifiedAt < todayStart)
                continue;

            any = true;
            Console.WriteLine($"\nInspection ID: {auditId}");
            Console.WriteLine($"  Created:   {root.GetProperty("created_at").GetString()}");

            // Print header details
            if (root.TryGetProperty("template_name", out var tplName))
                Console.WriteLine($"  Template: {tplName.GetString()}");
            if (root.TryGetProperty("audit_data", out var ad))
            {
                if (ad.TryGetProperty("completion_status", out var st))
                    Console.WriteLine($"  Status:   {st.GetString()}");
                if (ad.TryGetProperty("score", out var sc))
                    Console.WriteLine($"  Score:    {sc.GetDouble()}%");
            }

            // Updated timestamp
            Console.WriteLine($"  Updated:   {modProp.GetString()}");

            // Additional info
            if (root.TryGetProperty("location", out var loc) && !string.IsNullOrWhiteSpace(loc.GetString()))
                Console.WriteLine($"  Location: {loc.GetString()}");
            if (root.TryGetProperty("owner", out var owner))
                Console.WriteLine($"  Owner:    {owner.GetString()}");
            if (root.TryGetProperty("last_edited_by", out var editor))
                Console.WriteLine($"  Edited by: {editor.GetString()}");

            // Custom fields
            Console.WriteLine("  Custom fields:");
            bool found = false;
            ExtractCustomFields(root, (lbl, val) =>
            {
                found = true;
                Console.WriteLine($"    {lbl}: {val}");
            });
            if (!found)
                Console.WriteLine("    (none found)");
        }

        if (!any)
            Console.WriteLine($"No inspections updated since {todayStart:yyyy-MM-dd}.");
    }

    // Recursively find custom field labels
    static void ExtractCustomFields(JsonElement el, Action<string, string> onMatch)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            if (el.TryGetProperty("label", out var lbl) &&
                DesiredLabels.Contains(lbl.GetString() ?? string.Empty))
            {
                if (el.TryGetProperty("responses", out var resp))
                {
                    if (resp.TryGetProperty("text", out var t)) onMatch(lbl.GetString()!, t.GetString()!);
                    else if (resp.TryGetProperty("choice", out var c)) onMatch(lbl.GetString()!, c.GetString()!);
                    else if (resp.TryGetProperty("number", out var n)) onMatch(lbl.GetString()!, n.ToString());
                    else if (resp.TryGetProperty("datetime", out var d)) onMatch(lbl.GetString()!, d.GetString()!);
                }
            }
            foreach (var prop in el.EnumerateObject())
                if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    ExtractCustomFields(prop.Value, onMatch);
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
                ExtractCustomFields(item, onMatch);
        }
    }
}