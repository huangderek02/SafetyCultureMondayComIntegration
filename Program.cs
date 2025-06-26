using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SafetyCultureMondayIntegration
{
    public class AppConfig
    {
        public SafetyCultureConfig SafetyCulture { get; set; } = new SafetyCultureConfig();
        public MondayConfig Monday { get; set; } = new MondayConfig();
    }

    public class SafetyCultureConfig
    {
        public string ApiToken { get; set; } = "";
        public string TemplateId { get; set; } = "";
        public string BaseUrl { get; set; } = "https://api.safetyculture.io";
    }

    public class MondayConfig
    {
        public string ApiToken { get; set; } = "";
        public int BoardId { get; set; }
        public string ColumnName { get; set; } = "name";            // built-in name
        public string ColumnCreated { get; set; } = "date4";
        public string ColumnStatus { get; set; } = "color_mks6wn31";
        public string ColumnScore { get; set; } = "color_mks6vcdd";
        public string ColumnCompleted { get; set; } = "color_mks6p6hz";
        public string ColumnPartNumber { get; set; } = "color_mks6kz8f";
        public string ColumnQuantity { get; set; } = "color_mks6p9jh";
        public string ColumnTransaction { get; set; } = "color_mks6z1a6";
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

            cfg.SafetyCulture.ApiToken = cfg.SafetyCulture.ApiToken.Trim();
            cfg.Monday.ApiToken = cfg.Monday.ApiToken.Trim();

            // 1) HTTP clients
            using var sc = new HttpClient { BaseAddress = new Uri(cfg.SafetyCulture.BaseUrl) };
            sc.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", cfg.SafetyCulture.ApiToken);

            using var mc = new HttpClient { BaseAddress = new Uri("https://api.monday.com/v2") };
            mc.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", cfg.Monday.ApiToken);
            mc.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            // 2) Fetch audits
            Console.WriteLine("Fetching all audits...");
            var searchUrl = $"/audits/search?template={Uri.EscapeDataString(cfg.SafetyCulture.TemplateId)}"
                          + "&field=audit_id&field=modified_at"
                          + "&modified_after=1970-01-01T00:00:00Z";
            var listResp = await sc.GetAsync(searchUrl);
            listResp.EnsureSuccessStatusCode();
            var listJson = await listResp.Content.ReadAsStringAsync();
            var audits = JsonDocument.Parse(listJson)
                                     .RootElement
                                     .GetProperty("audits")
                                     .EnumerateArray()
                                     .ToList();
            Console.WriteLine($"Found {audits.Count} audits.\n");

            // 3) Process each
            foreach (var summary in audits)
            {
                string auditId = summary.GetProperty("audit_id").GetString()!;
                string itemName = $"Audit {auditId}";
                Console.WriteLine($"--- Processing {auditId} ---");

                try
                {
                    // a) Fetch full audit
                    var detResp = await sc.GetAsync($"/audits/{Uri.EscapeDataString(auditId)}");
                    detResp.EnsureSuccessStatusCode();
                    var detJson = await detResp.Content.ReadAsStringAsync();
                    var root = JsonDocument.Parse(detJson).RootElement;
                    var ad = root.GetProperty("audit_data");

                    // b) Core fields
                    string createdDate = DateTimeOffset
                        .Parse(root.GetProperty("created_at").GetString()!)
                        .ToString("yyyy-MM-dd");

                    string statusValue = ad.TryGetProperty("completion_status", out var cs)
                        ? cs.GetString()! : "UNKNOWN";

                    double scorePct = ad.GetProperty("score_percentage").GetDouble();

                    string compRaw = ad.TryGetProperty("completed_date", out var cd)
                        ? cd.GetString()!
                        : ad.TryGetProperty("completed_at", out var ca)
                          ? ca.GetString()!
                          : DateTimeOffset.UtcNow.ToString("o");
                    string completedDate = DateTimeOffset.Parse(compRaw)
                                            .ToString("yyyy-MM-dd");

                    // c) Headers
                    string partNumber = "";
                    int quantity = 0;
                    string transaction = "";

                    if (root.TryGetProperty("header_items", out var headers))
                    {
                        foreach (var hi in headers.EnumerateArray())
                        {
                            if (!hi.TryGetProperty("label", out var lblEl)
                             || !hi.TryGetProperty("responses", out var respEl))
                                continue;

                            var lbl = lblEl.GetString()!;
                            if (lbl.Equals("Part-Number", StringComparison.OrdinalIgnoreCase)
                             && respEl.TryGetProperty("text", out var txtP))
                            {
                                partNumber = txtP.GetString()!;
                            }
                            else if (lbl.Equals("Quantity", StringComparison.OrdinalIgnoreCase)
                                  && respEl.TryGetProperty("text", out var txtQ)
                                  && int.TryParse(txtQ.GetString(), out var qv))
                            {
                                quantity = qv;
                            }
                            else if (lbl.StartsWith("Trans", StringComparison.OrdinalIgnoreCase)
                                  && respEl.TryGetProperty("selected", out var sel)
                                  && sel.ValueKind == JsonValueKind.Array
                                  && sel.GetArrayLength() > 0)
                            {
                                var choice = sel[0];
                                transaction = choice.TryGetProperty("label", out var l2)
                                            ? l2.GetString()!
                                            : choice.GetProperty("value").GetString()!;
                            }
                        }
                    }

                    // d) Build column_values (omit audit_id)
                    var cols = new Dictionary<string, object>
                    {
                        [cfg.Monday.ColumnCreated] = new { date = createdDate },
                        [cfg.Monday.ColumnCompleted] = new { date = completedDate },
                        [cfg.Monday.ColumnStatus] = new { label = statusValue },
                        [cfg.Monday.ColumnScore] = scorePct
                    };
                    if (!string.IsNullOrEmpty(partNumber))
                        cols[cfg.Monday.ColumnPartNumber] = partNumber;
                    if (quantity > 0)
                        cols[cfg.Monday.ColumnQuantity] = quantity;
                    if (!string.IsNullOrEmpty(transaction))
                        cols[cfg.Monday.ColumnTransaction] = new { label = transaction };

                    Console.WriteLine(">>> column_values to send:\n"
                        + JsonSerializer.Serialize(cols, new JsonSerializerOptions { WriteIndented = true }));

                    // e) Upsert via `name`
                    var existing = await TryFindItem(mc, cfg.Monday.BoardId, cfg.Monday.ColumnName, itemName);
                    if (existing != null)
                    {
                        Console.WriteLine($"> Updating item #{existing}");
                        await ChangeMultiple(mc, existing, cfg.Monday.BoardId, cols);
                    }
                    else
                    {
                        Console.WriteLine("> Creating new item");
                        await CreateItem(mc, cfg.Monday.BoardId, itemName, cols);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"!!! ERROR for {auditId}: {ex.Message}");
                }
                Console.WriteLine();
            }

            Console.WriteLine("All done.");
        }

        static async Task<string?> TryFindItem(
            HttpClient client,
            int boardId,
            string columnId,
            string columnValue)
        {
            const string query = @"
query($boardId:ID!,$columnId:String!,$columnValue:String!){
  items_page_by_column_values(
    board_id:$boardId,
    columns:[{column_id:$columnId,column_values:[$columnValue]}],
    limit:1
  ){ items { id } }
}";
            var payload = JsonSerializer.Serialize(new
            {
                query,
                variables = new { boardId, columnId, columnValue }
            });

            Console.WriteLine(">>> FIND payload:\n" + payload);
            var resp = await client.PostAsync("", new StringContent(payload, Encoding.UTF8, "application/json"));
            var body = await resp.Content.ReadAsStringAsync();
            Console.WriteLine(">>> FIND response:\n" + body);

            using var doc = JsonDocument.Parse(body);
            var data = doc.RootElement.GetProperty("data")
                                      .GetProperty("items_page_by_column_values")
                                      .GetProperty("items");

            // **Safely** handle empty list:
            if (data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
            {
                return data[0].GetProperty("id").GetString();
            }
            return null;
        }

        static async Task CreateItem(
            HttpClient client,
            int boardId,
            string itemName,
            Dictionary<string, object> cols)
        {
            const string m = @"
mutation($b:ID!,$n:String!,$c:JSON!){
  create_item(board_id:$b,item_name:$n,column_values:$c){id}
}";
            // JSON! expects a string here:
            var colJson = JsonSerializer.Serialize(cols);
            var payload = JsonSerializer.Serialize(new
            {
                query = m,
                variables = new { b = boardId, n = itemName, c = colJson }
            });

            Console.WriteLine(">>> CREATE payload:\n" + payload);
            var resp = await client.PostAsync("", new StringContent(payload, Encoding.UTF8, "application/json"));
            var body = await resp.Content.ReadAsStringAsync();
            Console.WriteLine(">>> CREATE response:\n" + body);
            resp.EnsureSuccessStatusCode();
        }

        static async Task ChangeMultiple(
            HttpClient client,
            string itemId,
            int boardId,
            Dictionary<string, object> cols)
        {
            const string m = @"
mutation($i:ID!,$b:ID!,$c:JSON!){
  change_multiple_column_values(item_id:$i,board_id:$b,column_values:$c){id}
}";
            var colJson = JsonSerializer.Serialize(cols);
            var payload = JsonSerializer.Serialize(new
            {
                query = m,
                variables = new { i = itemId, b = boardId, c = colJson }
            });

            Console.WriteLine(">>> CHANGE payload:\n" + payload);
            var resp = await client.PostAsync("", new StringContent(payload, Encoding.UTF8, "application/json"));
            var body = await resp.Content.ReadAsStringAsync();
            Console.WriteLine(">>> CHANGE response:\n" + body);
            resp.EnsureSuccessStatusCode();
        }
    }
}
//using System;
//using System.IO;
//using System.Net.Http;
//using System.Net.Http.Headers;
//using System.Text;
//using System.Text.Json;
//using System.Threading.Tasks;

//namespace SafetyCultureMondayIntegration
//{
//    public class AppConfig
//    {
//        public MondayConfig Monday { get; set; } = new MondayConfig();
//    }

//    public class MondayConfig
//    {
//        public string ApiToken { get; set; } = "";
//        public int BoardId { get; set; }
//    }

//    class Program
//    {
//        static async Task Main()
//        {
//            // 0) Load config
//            if (!File.Exists("appsettings.json"))
//            {
//                Console.Error.WriteLine("ERROR: appsettings.json not found");
//                return;
//            }

//            string cfgJson = await File.ReadAllTextAsync("appsettings.json");
//            AppConfig cfg = JsonSerializer.Deserialize<AppConfig>(
//                cfgJson,
//                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
//            )!;

//            // 1) Prepare Monday.com client
//            cfg.Monday.ApiToken = cfg.Monday.ApiToken.Trim();
//            using HttpClient mc = new HttpClient { BaseAddress = new Uri("https://api.monday.com/v2") };
//            mc.DefaultRequestHeaders.Authorization =
//                new AuthenticationHeaderValue("Bearer", cfg.Monday.ApiToken);
//            mc.DefaultRequestHeaders.Accept.Add(
//                new MediaTypeWithQualityHeaderValue("application/json")
//            );

//            // 2) Print all column IDs → titles and exit
//            await PrintBoardColumns(mc, cfg.Monday.BoardId);
//        }

//        static async Task PrintBoardColumns(HttpClient client, int boardId)
//        {
//            const string query = @"
//                query($ids:[ID!]!) {
//                  boards(ids:$ids) {
//                    columns {
//                      id
//                      title
//                    }
//                  }
//                }";

//            var payload = new
//            {
//                query,
//                variables = new { ids = new[] { boardId.ToString() } }
//            };

//            string json = JsonSerializer.Serialize(payload);

//            using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, "")
//            {
//                Content = new StringContent(json, Encoding.UTF8, "application/json")
//            };

//            HttpResponseMessage resp = await client.SendAsync(req);
//            string body = await resp.Content.ReadAsStringAsync();

//            if (!resp.IsSuccessStatusCode)
//            {
//                Console.Error.WriteLine($"Error {resp.StatusCode}:\n{body}");
//                return;
//            }

//            using JsonDocument doc = JsonDocument.Parse(body);
//            JsonElement cols = doc.RootElement
//                                 .GetProperty("data")
//                                 .GetProperty("boards")[0]
//                                 .GetProperty("columns");

//            Console.WriteLine("Board columns (id → title):");
//            foreach (JsonElement col in cols.EnumerateArray())
//            {
//                string id = col.GetProperty("id").GetString()!;
//                string title = col.GetProperty("title").GetString()!;
//                Console.WriteLine($"  {id}  →  {title}");
//            }
//        }
//    }
//}
