﻿using System;
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
        public SafetyCultureConfig SafetyCulture { get; set; } = new();
        public MondayConfig Monday { get; set; } = new();
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

        public string ColumnName { get; set; } = "name";
        public string ColumnCreated { get; set; } = "date4";
        public string ColumnCompleted { get; set; } = "date_mksahg27";
        public string ColumnScore { get; set; } = "numeric_mksczk53";
        public string ColumnQuantity { get; set; } = "numeric_mkscjk7r";
        public string ColumnPartNumber { get; set; } = "text_mksaxab";
        public string ColumnTransaction { get; set; } = "text_mksakm5d";
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

            // Trim API tokens
            cfg.SafetyCulture.ApiToken = cfg.SafetyCulture.ApiToken.Trim();
            cfg.Monday.ApiToken = cfg.Monday.ApiToken.Trim();

            // 1) SafetyCulture client
            using var sc = new HttpClient { BaseAddress = new Uri(cfg.SafetyCulture.BaseUrl) };
            sc.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", cfg.SafetyCulture.ApiToken);

            // 2) Monday.com client
            using var mc = new HttpClient { BaseAddress = new Uri("https://api.monday.com/v2") };
            mc.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", cfg.Monday.ApiToken);
            mc.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            // 3) Fetch audits
            Console.WriteLine("Fetching all audits…");
            var listResp = await sc.GetAsync(
                $"/audits/search?template={Uri.EscapeDataString(cfg.SafetyCulture.TemplateId)}" +
                "&field=audit_id&field=modified_at&modified_after=1970-01-01T00:00:00Z"
            );
            listResp.EnsureSuccessStatusCode();
            var listJson = await listResp.Content.ReadAsStringAsync();
            var audits = JsonDocument.Parse(listJson)
                                     .RootElement
                                     .GetProperty("audits")
                                     .EnumerateArray()
                                     .ToList();
            Console.WriteLine($"Found {audits.Count} audits.\n");

            // 4) Process each
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

                    double rawScore = ad.GetProperty("score_percentage").GetDouble();
                    int scorePct = Convert.ToInt32(Math.Round(rawScore));

                    string compRaw = ad.TryGetProperty("completed_date", out var cd)
                        ? cd.GetString()!
                        : ad.TryGetProperty("completed_at", out var ca)
                            ? ca.GetString()!
                            : DateTimeOffset.UtcNow.ToString("o");
                    string completedDate = DateTimeOffset.Parse(compRaw).ToString("yyyy-MM-dd");

                    // c) Normalize completion_status → "complete"/"incomplete"
                    string rawStatus = ad.TryGetProperty("completion_status", out var cs)
                        ? cs.GetString()!
                        : "";
                    string statusValue = rawStatus.Equals("completed", StringComparison.OrdinalIgnoreCase)
                        ? "complete"
                        : "incomplete";

                    // d) Header items
                    string partNumber = "";
                    int quantity = 0;
                    string transaction = "";

                    if (root.TryGetProperty("header_items", out var headers))
                    {
                        foreach (var hi in headers.EnumerateArray())
                        {
                            if (!hi.TryGetProperty("label", out var lbl) ||
                                !hi.TryGetProperty("responses", out var resp))
                                continue;
                            var label = lbl.GetString()!;
                            if (label.Equals("Part-Number", StringComparison.OrdinalIgnoreCase)
                             && resp.TryGetProperty("text", out var tP))
                            {
                                partNumber = tP.GetString()!;
                            }
                            else if (label.Equals("Quantity", StringComparison.OrdinalIgnoreCase)
                                  && resp.TryGetProperty("text", out var tQ)
                                  && int.TryParse(tQ.GetString(), out var qv))
                            {
                                quantity = qv;
                            }
                            else if (label.StartsWith("Trans", StringComparison.OrdinalIgnoreCase)
                                  && resp.TryGetProperty("selected", out var sel)
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

                    // e) Build column_values
                    var cols = new Dictionary<string, object>
                    {
                        [cfg.Monday.ColumnCreated] = new { date = createdDate },
                        [cfg.Monday.ColumnCompleted] = new { date = completedDate },
                        [cfg.Monday.ColumnScore] = scorePct,      // NUMBERS
                        [cfg.Monday.ColumnQuantity] = quantity,      // NUMBERS
                        [cfg.Monday.ColumnPartNumber] = partNumber,    // TEXT
                        [cfg.Monday.ColumnTransaction] = transaction    // TEXT
                    };

                    Console.WriteLine(">>> column_values:\n" +
                        JsonSerializer.Serialize(cols, new JsonSerializerOptions { WriteIndented = true }));

                    // f) Upsert by name
                    var existing = await TryFindItem(mc, cfg.Monday.BoardId,
                                                     cfg.Monday.ColumnName, itemName);
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

        static async Task<string?> TryFindItem(HttpClient client, int boardId, string columnId, string columnValue)
        {
            const string Q = @"
query($boardId:ID!,$columnId:String!,$columnValue:String!){
  items_page_by_column_values(
    board_id:$boardId,
    columns:[{column_id:$columnId,column_values:[$columnValue]}],
    limit:1
  ){ items { id } }
}";
            var payload = JsonSerializer.Serialize(new
            {
                query = Q,
                variables = new { boardId, columnId, columnValue }
            });

            var resp = await client.PostAsync("",
                new StringContent(payload, Encoding.UTF8, "application/json"));
            var body = await resp.Content.ReadAsStringAsync();
            Console.WriteLine(">>> FIND response:\n" + body);

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("errors", out var errs))
            {
                Console.WriteLine("GraphQL errors:\n" + errs);
                return null;
            }

            var items = doc.RootElement
                           .GetProperty("data")
                           .GetProperty("items_page_by_column_values")
                           .GetProperty("items");

            if (items.ValueKind == JsonValueKind.Array && items.GetArrayLength() > 0)
                return items[0].GetProperty("id").GetString();
            return null;
        }

        static async Task CreateItem(HttpClient client, int boardId, string itemName, Dictionary<string, object> cols)
        {
            const string M = @"
mutation($b:ID!,$n:String!,$c:JSON!){
  create_item(board_id:$b,item_name:$n,column_values:$c){id}
}";
            var colJson = JsonSerializer.Serialize(cols);
            var payload = JsonSerializer.Serialize(new
            {
                query = M,
                variables = new { b = boardId, n = itemName, c = colJson }
            });

            var resp = await client.PostAsync("",
                new StringContent(payload, Encoding.UTF8, "application/json"));
            var body = await resp.Content.ReadAsStringAsync();
            Console.WriteLine(">>> CREATE response:\n" + body);

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("errors", out var errs))
                throw new Exception("GraphQL errors on create:\n" + errs);

            var id = doc.RootElement
                        .GetProperty("data")
                        .GetProperty("create_item")
                        .GetProperty("id")
                        .GetString();
            Console.WriteLine($"> Created item #{id}");
        }

        static async Task ChangeMultiple(HttpClient client, string itemId, int boardId, Dictionary<string, object> cols)
        {
            const string M = @"
mutation($i:ID!,$b:ID!,$c:JSON!){
  change_multiple_column_values(item_id:$i,board_id:$b,column_values:$c){id}
}";
            var colJson = JsonSerializer.Serialize(cols);
            var payload = JsonSerializer.Serialize(new
            {
                query = M,
                variables = new { i = itemId, b = boardId, c = colJson }
            });

            var resp = await client.PostAsync("",
                new StringContent(payload, Encoding.UTF8, "application/json"));
            var body = await resp.Content.ReadAsStringAsync();
            Console.WriteLine(">>> CHANGE response:\n" + body);

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("errors", out var errs))
                throw new Exception("GraphQL errors on update:\n" + errs);

            var id = doc.RootElement
                        .GetProperty("data")
                        .GetProperty("change_multiple_column_values")
                        .GetProperty("id")
                        .GetString();
            Console.WriteLine($"> Updated item #{id}");
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