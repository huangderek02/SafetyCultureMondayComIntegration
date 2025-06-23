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
        public string ColumnAuditId { get; set; } = "audit_id";              // replace with real column ID
        public string ColumnCreated { get; set; } = "created_at";            // replace with real column ID
        public string ColumnStatus { get; set; } = "completion_status";      // replace with real column ID
        public string ColumnScore { get; set; } = "score_percentage";        // replace with real column ID
        public string ColumnCompleted { get; set; } = "completed_at";        // replace with real column ID
        public string ColumnPartNumber { get; set; } = "part_number";        // replace with real column ID
        public string ColumnTransaction { get; set; } = "transaction_type";  // replace with real column ID
        public string ColumnQuantity { get; set; } = "quantity";             // replace with real column ID
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

            string configJson = await File.ReadAllTextAsync("appsettings.json");
            AppConfig config = JsonSerializer.Deserialize<AppConfig>(
                configJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            )!;

            // Trim whitespace from tokens
            config.SafetyCulture.ApiToken = config.SafetyCulture.ApiToken.Trim();
            config.Monday.ApiToken = config.Monday.ApiToken.Trim();

            // 1) Prepare HTTP clients
            HttpClient sc = new HttpClient { BaseAddress = new Uri(config.SafetyCulture.BaseUrl) };
            sc.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", config.SafetyCulture.ApiToken);

            HttpClient mc = new HttpClient { BaseAddress = new Uri("https://api.monday.com/v2") };
            mc.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", config.Monday.ApiToken);
            mc.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json")
            );

            // 2) Fetch all audits from SafetyCulture
            Console.WriteLine("Fetching all audits...");
            string searchUrl = "/audits/search"
                             + "?template=" + Uri.EscapeDataString(config.SafetyCulture.TemplateId)
                             + "&field=audit_id&field=modified_at"
                             + "&modified_after=1970-01-01T00:00:00Z";

            HttpResponseMessage listResponse = await sc.GetAsync(searchUrl);
            string listJson = await listResponse.Content.ReadAsStringAsync();
            listResponse.EnsureSuccessStatusCode();

            JsonDocument listDoc = JsonDocument.Parse(listJson);
            JsonElement auditsArray = listDoc.RootElement.GetProperty("audits");
            List<JsonElement> audits = new List<JsonElement>();
            foreach (JsonElement element in auditsArray.EnumerateArray())
            {
                audits.Add(element);
            }
            Console.WriteLine($"Found {audits.Count} audits.\n");

            // 3) Process each audit
            foreach (JsonElement summary in audits)
            {
                string auditId = summary.GetProperty("audit_id").GetString()!;
                Console.WriteLine($"Processing audit_id = {auditId}");

                // 3a) Fetch full audit
                string detailUrl = "/audits/" + Uri.EscapeDataString(auditId);
                HttpResponseMessage detailResponse = await sc.GetAsync(detailUrl);
                string detailJson = await detailResponse.Content.ReadAsStringAsync();
                detailResponse.EnsureSuccessStatusCode();

                JsonDocument detailDoc = JsonDocument.Parse(detailJson);
                JsonElement root = detailDoc.RootElement;
                JsonElement auditData = root.GetProperty("audit_data");

                // 3b) Extract fields
                // Created At
                string createdRaw = root.GetProperty("created_at").GetString() ?? "";
                DateTimeOffset createdDt = DateTimeOffset.Parse(createdRaw);
                string createdDate = createdDt.ToString("yyyy-MM-dd");

                // Status
                string statusValue;
                if (auditData.TryGetProperty("completion_status", out JsonElement csElem))
                {
                    statusValue = csElem.GetString()!;
                }
                else
                {
                    statusValue = "UNKNOWN";
                }

                // Score Percentage
                double scorePercentage = auditData.GetProperty("score_percentage").GetDouble();

                // Completed At
                string completedRaw;
                if (auditData.TryGetProperty("completed_date", out JsonElement cdElem))
                {
                    completedRaw = cdElem.GetString()!;
                }
                else if (auditData.TryGetProperty("completed_at", out JsonElement caElem))
                {
                    completedRaw = caElem.GetString()!;
                }
                else
                {
                    completedRaw = DateTimeOffset.UtcNow.ToString("o");
                }
                DateTimeOffset completedDt = DateTimeOffset.Parse(completedRaw);
                string completedDate = completedDt.ToString("yyyy-MM-dd");

                // Build column payload
                Dictionary<string, object> columns = new Dictionary<string, object>
                {
                    { config.Monday.ColumnAuditId,   auditId },
                    { config.Monday.ColumnCreated,   createdDate },
                    { config.Monday.ColumnStatus,    statusValue },
                    { config.Monday.ColumnScore,     scorePercentage },
                    { config.Monday.ColumnCompleted, completedDate }
                };

                // Extract Part-Number and Quantity from template_data.header_items
                if (root.TryGetProperty("template_data", out JsonElement tplData)
                    && tplData.TryGetProperty("header_items", out JsonElement headerItems)
                    && headerItems.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in headerItems.EnumerateArray())
                    {
                        string labelText = item.GetProperty("label").GetString()!;
                        if (labelText.Equals("Part-Number", StringComparison.OrdinalIgnoreCase)
                            && item.TryGetProperty("responses", out JsonElement respP)
                            && respP.TryGetProperty("text", out JsonElement txtP))
                        {
                            columns[config.Monday.ColumnPartNumber] = txtP.GetString()!;
                        }
                        if (labelText.Equals("Quantity", StringComparison.OrdinalIgnoreCase)
                            && item.TryGetProperty("responses", out JsonElement respQ)
                            && respQ.TryGetProperty("text", out JsonElement txtQ)
                            && int.TryParse(txtQ.GetString(), out int qtyValue))
                        {
                            columns[config.Monday.ColumnQuantity] = qtyValue;
                        }
                    }
                }

                // Extract Transaction Type from audit_data.item_responses
                if (auditData.TryGetProperty("item_responses", out JsonElement itemResponses)
                    && itemResponses.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement respItem in itemResponses.EnumerateArray())
                    {
                        string itemLabel = respItem.GetProperty("label").GetString()!;
                        if (itemLabel == "Transaction Type"
                            && respItem.TryGetProperty("response_data", out JsonElement rd)
                            && rd.TryGetProperty("choices", out JsonElement choices)
                            && choices.ValueKind == JsonValueKind.Array
                            && choices.GetArrayLength() > 0)
                        {
                            string txValue = choices[0].GetProperty("value").GetString()!;
                            columns[config.Monday.ColumnTransaction] = txValue;
                            break;
                        }
                    }
                }

                Console.WriteLine("Final column payload:");
                string prettyCols = JsonSerializer.Serialize(
                    columns,
                    new JsonSerializerOptions { WriteIndented = true }
                );
                Console.WriteLine(prettyCols);

                // 4) Upsert into Monday.com
                string? existingItemId = await FindMondayItem(
                    mc,
                    config.Monday.BoardId,
                    config.Monday.ColumnAuditId,
                    auditId
                );

                if (existingItemId != null)
                {
                    Console.WriteLine($"Updating item {existingItemId}");
                    await ChangeMultiple(mc, existingItemId, config.Monday.BoardId, columns);
                }
                else
                {
                    Console.WriteLine("Creating new item");
                    await CreateItem(mc, config.Monday.BoardId, auditId, columns);
                }

                Console.WriteLine();
            }

            Console.WriteLine("Done.");
        }

        static async Task<string?> FindMondayItem(
            HttpClient client,
            int boardId,
            string columnId,
            string columnValue)
        {
            string query =
                "query($b:ID!,$c:String!,$v:String!){"
              + " items_page_by_column_values("
              + "   board_id:$b,"
              + "   columns:[{column_id:$c,column_values:[$v]}],"
              + "   limit:1"
              + " ){ items { id } }"
              + "}";

            string payload = JsonSerializer.Serialize(new
            {
                query,
                variables = new { b = boardId, c = columnId, v = columnValue }
            });

            HttpRequestMessage request = new HttpRequestMessage(
                HttpMethod.Post,
                ""
            );
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            HttpResponseMessage response = await client.SendAsync(request);
            string responseBody = await response.Content.ReadAsStringAsync();
            response.EnsureSuccessStatusCode();

            JsonDocument doc = JsonDocument.Parse(responseBody);
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("errors", out JsonElement errors))
            {
                return null;
            }

            JsonElement data = root.GetProperty("data");
            JsonElement page = data.GetProperty("items_page_by_column_values");
            JsonElement items = page.GetProperty("items");

            if (items.GetArrayLength() == 0)
            {
                return null;
            }

            JsonElement first = items[0];
            return first.GetProperty("id").GetString();
        }

        static async Task CreateItem(
            HttpClient client,
            int boardId,
            string itemName,
            Dictionary<string, object> cols)
        {
            string mutation =
                "mutation($b:ID!,$n:String!,$c:JSON!){"
              + " create_item(board_id:$b,item_name:$n,column_values:$c){id}"
              + "}";

            string payload = JsonSerializer.Serialize(new
            {
                query = mutation,
                variables = new
                {
                    b = boardId,
                    n = itemName,
                    c = JsonSerializer.Serialize(cols)
                }
            });

            HttpRequestMessage request = new HttpRequestMessage(
                HttpMethod.Post,
                ""
            );
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            HttpResponseMessage response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }

        static async Task ChangeMultiple(
            HttpClient client,
            string itemId,
            int boardId,
            Dictionary<string, object> cols)
        {
            string mutation =
                "mutation($i:ID!,$b:ID!,$c:JSON!){"
              + " change_multiple_column_values("
              + "   item_id:$i,"
              + "   board_id:$b,"
              + "   column_values:$c"
              + " ){id}"
              + "}";

            string payload = JsonSerializer.Serialize(new
            {
                query = mutation,
                variables = new
                {
                    i = itemId,
                    b = boardId,
                    c = JsonSerializer.Serialize(cols)
                }
            });

            HttpRequestMessage request = new HttpRequestMessage(
                HttpMethod.Post,
                ""
            );
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            HttpResponseMessage response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }
    }
}