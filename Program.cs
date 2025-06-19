using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Collections.Generic;
using System.Threading.Tasks;

// Configuration classes
public class AppConfig
{
    public SafetyCultureConfig SafetyCulture { get; set; } = new SafetyCultureConfig();
}

public class SafetyCultureConfig
{
    public string ApiToken { get; set; } = string.Empty;
    public string TemplateId { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.safetyculture.io";
}

class Program
{
    // Custom fields to extract
    static readonly HashSet<string> DesiredLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "Part-Number", "Transaction Type", "Quantity"
    };

    static async Task Main()
    {
        // 1) Load configuration from JSON or environment
        AppConfig config;
        if (File.Exists("appsettings.json"))
        {
            var json = await File.ReadAllTextAsync("appsettings.json");
            config = JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? throw new Exception("Failed to parse appsettings.json");
        }
        else
        {
            // Fallback to environment variables
            config = new AppConfig
            {
                SafetyCulture = new SafetyCultureConfig
                {
                    ApiToken = Environment.GetEnvironmentVariable("SAFETYCULTURE_API_TOKEN") ?? throw new Exception("Env var SAFETYCULTURE_API_TOKEN required"),
                    TemplateId = Environment.GetEnvironmentVariable("SAFETYCULTURE_TEMPLATE_ID") ?? throw new Exception("Env var SAFETYCULTURE_TEMPLATE_ID required"),
                    BaseUrl = Environment.GetEnvironmentVariable("SAFETYCULTURE_BASE_URL") ?? "https://api.safetyculture.io"
                }
            };
        }

        var sc = config.SafetyCulture;
        using var client = new HttpClient { BaseAddress = new Uri(sc.BaseUrl) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", sc.ApiToken);

        // 2) Compute start of today (UTC)
        DateTimeOffset todayStart = DateTimeOffset.UtcNow.Date;
        Console.WriteLine($"Inspections updated since {todayStart:yyyy-MM-dd} (UTC):\n");

        // 3) Fetch summary list with modified_at
        string listUrl = $"/audits/search?template={sc.TemplateId}&field=audit_id&field=modified_at";
        var listResp = await client.GetAsync(listUrl);
        listResp.EnsureSuccessStatusCode();
        using var listDoc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync());
        var audits = listDoc.RootElement.GetProperty("audits");

        bool any = false;

        // 4) Iterate summary and filter by modified_at >= todayStart
        foreach (var a in audits.EnumerateArray())
        {
            if (!a.TryGetProperty("audit_id", out var idElem) || !a.TryGetProperty("modified_at", out var modElem))
                continue;
            if (!DateTimeOffset.TryParse(modElem.GetString(), out var modifiedAt) || modifiedAt < todayStart)
                continue;

            any = true;
            string auditId = idElem.GetString()!;
            Console.WriteLine($"--- Audit ID: {auditId} (modified: {modifiedAt:O}) ---");

            // 5) Fetch full details
            var detResp = await client.GetAsync($"/audits/{auditId}");
            detResp.EnsureSuccessStatusCode();
            using var detDoc = JsonDocument.Parse(await detResp.Content.ReadAsStringAsync());
            var root = detDoc.RootElement;

            // Print core metadata
            if (root.TryGetProperty("created_at", out var crt))
                Console.WriteLine($"Created:  {crt.GetString()}");
            if (root.TryGetProperty("audit_data", out var ad))
            {
                if (ad.TryGetProperty("completion_status", out var st))
                    Console.WriteLine($"Status:   {st.GetString()}");
                if (ad.TryGetProperty("score_percentage", out var pc))
                    Console.WriteLine($"Score:    {pc.GetDouble()}%");
            }

            // Extract custom fields
            Console.WriteLine("Custom fields:");
            bool found = false;
            ExtractCustomFields(root, (lbl, val) =>
            {
                found = true;
                Console.WriteLine($"  {lbl}: {val}");
            });
            if (!found)
                Console.WriteLine("  (none found)");

            Console.WriteLine();
        }

        if (!any)
            Console.WriteLine("No inspections updated today.");
    }

    static void ExtractCustomFields(JsonElement el, Action<string, string> onMatch)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            if (el.TryGetProperty("label", out var lbl) && DesiredLabels.Contains(lbl.GetString() ?? string.Empty))
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
                ExtractCustomFields(prop.Value, onMatch);
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
                ExtractCustomFields(item, onMatch);
        }
    }
}