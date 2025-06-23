using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace SafetyCultureMondayIntegration
{
    public class AppConfig
    {
        public SafetyCultureConfig SafetyCulture { get; set; } = new();
        public DatabaseConfig Database { get; set; } = new();
        public MondayConfig Monday { get; set; } = new();
    }

    public class SafetyCultureConfig
    {
        public string ApiToken { get; set; } = "";
        public string TemplateId { get; set; } = "";
        public string BaseUrl { get; set; } = "https://api.safetyculture.io";
    }

    public class DatabaseConfig
    {
        public string ConnectionString { get; set; } = "Data Source=inspections.db";
    }

    public class MondayConfig
    {
        public string ApiToken { get; set; } = "";
        public int BoardId { get; set; }
        public string ColumnAuditId { get; set; } = "audit_id";
        public string ColumnCreated { get; set; } = "created_at";
        public string ColumnStatus { get; set; } = "completion_status";
        public string ColumnScore { get; set; } = "score_percentage";
        public string ColumnCompleted { get; set; } = "completed_at";
        public string ColumnPartNumber { get; set; } = "part_number";
        public string ColumnTransaction { get; set; } = "transaction_type";
        public string ColumnQuantity { get; set; } = "quantity";
    }

    class Program
    {
        static async Task Main()
        {
            // 0) Load config
            if (!File.Exists("appsettings.json"))
            {
                Console.Error.WriteLine("ERROR: appsettings.json not found");
                return;
            }

            var cfg = JsonSerializer.Deserialize<AppConfig>(
                await File.ReadAllTextAsync("appsettings.json"),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            )!;

            // Trim tokens
            cfg.SafetyCulture.ApiToken = cfg.SafetyCulture.ApiToken.Trim();
            cfg.Monday.ApiToken = cfg.Monday.ApiToken.Trim();

            // 1) Prepare HTTP clients
            using var sc = new HttpClient { BaseAddress = new Uri(cfg.SafetyCulture.BaseUrl) };
            sc.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", cfg.SafetyCulture.ApiToken);

            using var mc = new HttpClient { BaseAddress = new Uri("https://api.monday.com/v2") };
            mc.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", cfg.Monday.ApiToken);
            mc.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json")
            );

            // 2) Open & migrate SQLite
            using var db = new SqliteConnection(cfg.Database.ConnectionString);
            db.Open();
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS inspections (
  audit_id      TEXT PRIMARY KEY,
  completed_at  TEXT,
  payload       TEXT
);";
                cmd.ExecuteNonQuery();
            }

            // 3) Compute cutoff
            DateTimeOffset cutoff = DateTimeOffset.UtcNow.Date;
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "SELECT MAX(completed_at) FROM inspections;";
                var o = cmd.ExecuteScalar() as string;
                if (!string.IsNullOrEmpty(o) && DateTimeOffset.TryParse(o, out var last))
                    cutoff = last;
            }
            Console.WriteLine($"Syncing audits completed since {cutoff:O}\n");

            // 4) Fetch modified summary
            var sinceIso = cutoff.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            var searchUrl = $"/audits/search?template={cfg.SafetyCulture.TemplateId}&field=audit_id&field=modified_at&modified_after={Uri.EscapeDataString(sinceIso)}";

            Console.WriteLine($"DEBUG: GET {searchUrl}");
            var listResp = await sc.GetAsync(searchUrl);
            var listJson = await listResp.Content.ReadAsStringAsync();
            Console.WriteLine($"DEBUG: Summary JSON:\n{listJson}\n");
            listResp.EnsureSuccessStatusCode();

            using var listDoc = JsonDocument.Parse(listJson);
            var summaries = listDoc.RootElement.GetProperty("audits").EnumerateArray().ToList();
            Console.WriteLine($"Found {summaries.Count} modified audit summaries.\n");

            // 5) Process each summary
            foreach (var s in summaries)
            {
                var auditId = s.GetProperty("audit_id").GetString()!;
                var modDt = DateTimeOffset.Parse(s.GetProperty("modified_at").GetString()!);
                Console.WriteLine($"---- Processing audit_id={auditId}, modified_at={modDt:O} ----");

                // 5a) Fetch full audit
                var detailUrl = $"/audits/{auditId}";
                Console.WriteLine($"DEBUG: GET {detailUrl}");
                var detResp = await sc.GetAsync(detailUrl);
                var detJson = await detResp.Content.ReadAsStringAsync();
                Console.WriteLine($"DEBUG: Detail JSON:\n{detJson}\n");
                detResp.EnsureSuccessStatusCode();

                using var detDoc = JsonDocument.Parse(detJson);
                var root = detDoc.RootElement;
                var ad = root.GetProperty("audit_data");

                // 5b) Determine status & completion
                var status = ad.TryGetProperty("completion_status", out var cs) ? cs.GetString()! : "UNKNOWN";
                var compS = ad.TryGetProperty("completed_date", out var cd) ? cd.GetString()!
                           : ad.TryGetProperty("completed_at", out var ca) ? ca.GetString()!
                           : modDt.ToString("o");
                var compDt = DateTimeOffset.TryParse(compS, out var tmp) ? tmp : modDt;
                Console.WriteLine($"  status={status}, effective completed_at={compDt:O}");

                // 6) Upsert into SQLite
                using (var up = db.CreateCommand())
                {
                    up.CommandText = @"
INSERT INTO inspections(audit_id,completed_at,payload)
VALUES($id,$comp,$p)
ON CONFLICT(audit_id) DO UPDATE
  SET completed_at=$comp,payload=$p;";
                    up.Parameters.AddWithValue("$id", auditId);
                    up.Parameters.AddWithValue("$comp", compS);
                    up.Parameters.AddWithValue("$p", root.GetRawText());
                    up.ExecuteNonQuery();
                }
                Console.WriteLine("  SQLite upsert complete.\n");

                // 7) Build Monday.com columns
                var cols = new Dictionary<string, object>
                {
                    [cfg.Monday.ColumnAuditId] = auditId,
                    [cfg.Monday.ColumnCreated] = root.GetProperty("created_at").GetString() ?? string.Empty,
                    [cfg.Monday.ColumnStatus] = status,
                    [cfg.Monday.ColumnScore] = ad.GetProperty("score_percentage").GetDouble(),
                    [cfg.Monday.ColumnCompleted] = compDt.ToString("yyyy-MM-dd")
                };
                ExtractCustom(root, (lbl, val) =>
                {
                    if (lbl.Equals("Part-Number", StringComparison.OrdinalIgnoreCase))
                        cols[cfg.Monday.ColumnPartNumber] = val;
                    if (lbl.Equals("Quantity", StringComparison.OrdinalIgnoreCase) && double.TryParse(val, out var q))
                        cols[cfg.Monday.ColumnQuantity] = q;
                    if (lbl.Equals("Transaction Type", StringComparison.OrdinalIgnoreCase))
                        cols[cfg.Monday.ColumnTransaction] = val;
                });
                Console.WriteLine($"  Final column payload:\n{JsonSerializer.Serialize(cols, new JsonSerializerOptions { WriteIndented = true })}\n");

                // 8) Upsert into Monday.com
                var existing = await FindMondayItem(mc, cfg.Monday.BoardId, cfg.Monday.ColumnAuditId, auditId);
                if (existing != null)
                {
                    Console.WriteLine($"  DEBUG: Updating item {existing}");
                    await ChangeMultiple(mc, existing, cfg.Monday.BoardId, cols);
                }
                else
                {
                    Console.WriteLine("  DEBUG: Creating new item");
                    await CreateItem(mc, cfg.Monday.BoardId, $"Audit {auditId}", cols);
                }

                Console.WriteLine();
            }

            Console.WriteLine("Done.");
        }

        static void ExtractCustom(JsonElement el, Action<string, string> onMatch)
        {
            if (el.ValueKind == JsonValueKind.Object)
            {
                if (el.TryGetProperty("label", out var L) && el.TryGetProperty("responses", out var R))
                {
                    var lbl = L.GetString()!;
                    if (R.TryGetProperty("text", out var t)) onMatch(lbl, t.GetString()!);
                    if (R.TryGetProperty("choice", out var c)) onMatch(lbl, c.GetString()!);
                    if (R.TryGetProperty("number", out var n)) onMatch(lbl, n.ToString());
                }
                foreach (var p in el.EnumerateObject()) ExtractCustom(p.Value, onMatch);
            }
            else if (el.ValueKind == JsonValueKind.Array)
                foreach (var i in el.EnumerateArray()) ExtractCustom(i, onMatch);
        }

        static async Task<string?> FindMondayItem(
    HttpClient client,
    int board,
    string col,
    string val)
        {
            // Use the new v2 field and argument shape
            const string query =
              "query($boardId:ID!,$columnId:String!,$columnValue:String!){"
            + "  items_page_by_column_values("
            + "    board_id:$boardId,"
            + "    columns:[{column_id:$columnId,column_values:[$columnValue]}],"
            + "    limit:1"
            + "  ){ items { id } }"
            + "}";

            var payload = JsonSerializer.Serialize(new
            {
                query,
                variables = new
                {
                    boardId = board,
                    columnId = col,
                    columnValue = val
                }
            });

            Console.WriteLine("=== Find Payload ===");
            Console.WriteLine(payload);
            Console.WriteLine("=== Request Headers ===");
            foreach (var h in client.DefaultRequestHeaders)
                Console.WriteLine($"{h.Key}: {string.Join(",", h.Value)}");

            var resp = await client.PostAsync(
                "",
                new StringContent(payload, Encoding.UTF8, "application/json")
            );
            var body = await resp.Content.ReadAsStringAsync();
            Console.WriteLine("=== Find Response ===");
            Console.WriteLine(body);

            // Try to parse JSON
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // 1) If GraphQL errors are present, log & bail out:
            if (root.TryGetProperty("errors", out var errs))
            {
                Console.WriteLine("GraphQL errors:");
                foreach (var err in errs.EnumerateArray())
                    Console.WriteLine("  - " + err.GetProperty("message").GetString());
                return null;
            }

            // 2) Drill into data → items_page_by_column_values → items
            if (!root.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("items_page_by_column_values", out var page))
            {
                Console.WriteLine("No 'items_page_by_column_values' in response.");
                return null;
            }

            if (!page.TryGetProperty("items", out var itemsArr) ||
                itemsArr.GetArrayLength() == 0)
            {
                Console.WriteLine("No items returned.");
                return null;
            }

            // 3) Pull out the first ID
            return itemsArr.EnumerateArray()
                           .Select(item => item.GetProperty("id").GetString())
                           .FirstOrDefault();
        }

        static async Task CreateItem(HttpClient client, int board, string name, Dictionary<string, object> cols)
        {
            const string m = "mutation($b:ID!,$n:String!,$c:JSON!){create_item(board_id:$b,item_name:$n,column_values:$c){id}}";
            var payload = JsonSerializer.Serialize(new { query = m, variables = new { b = board, n = name, c = JsonSerializer.Serialize(cols) } });
            var resp = await client.PostAsync("", new StringContent(payload, Encoding.UTF8, "application/json"));
            resp.EnsureSuccessStatusCode();
        }

        static async Task ChangeMultiple(HttpClient client, string itemId, int board, Dictionary<string, object> cols)
        {
            const string m = "mutation($i:ID!,$b:ID!,$c:JSON!){change_multiple_column_values(item_id:$i,board_id:$b,column_values:$c){id}}";
            var payload = JsonSerializer.Serialize(new { query = m, variables = new { i = itemId, b = board, c = JsonSerializer.Serialize(cols) } });
            var resp = await client.PostAsync("", new StringContent(payload, Encoding.UTF8, "application/json"));
            resp.EnsureSuccessStatusCode();
        }
    }
}