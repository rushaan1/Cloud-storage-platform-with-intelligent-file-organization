using CloudStoragePlatform.Core.ServiceContracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudStoragePlatform.Core.Services.Ai
{
    /// <summary>
    /// Thin REST client for the Pinecone data plane (per-index host).
    /// Configures BaseAddress + Api-Key header from configuration on construction.
    /// </summary>
    public class PineconeClient : IPineconeClient
    {
        private readonly HttpClient _http;
        private readonly ILogger<PineconeClient> _logger;

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = null
        };

        public PineconeClient(HttpClient http, IConfiguration config, ILogger<PineconeClient> logger)
        {
            _http = http;
            _logger = logger;
            string? host = config["Ai:Pinecone:IndexHost"];
            string? apiKey = config["Pinecone:ApiKey"];
            if (string.IsNullOrWhiteSpace(host))
                throw new InvalidOperationException("Ai:Pinecone:IndexHost is not configured.");
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("Pinecone:ApiKey is not configured (user-secrets or KeyVault).");

            if (!host.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                host = "https://" + host;
            _http.BaseAddress = new Uri(host);
            _http.DefaultRequestHeaders.Remove("Api-Key");
            _http.DefaultRequestHeaders.Add("Api-Key", apiKey);
            _http.DefaultRequestHeaders.Remove("X-Pinecone-API-Version");
            _http.DefaultRequestHeaders.Add("X-Pinecone-API-Version", "2025-01");
        }

        public Task UpsertAsync(string id, float[] vector, IDictionary<string, object> metadata, CancellationToken ct = default)
            => UpsertBatchAsync(new[] { new PineconeUpsertItem(id, vector, metadata) }, ct);

        public async Task UpsertBatchAsync(IEnumerable<PineconeUpsertItem> items, CancellationToken ct = default)
        {
            var body = new
            {
                vectors = items.Select(i => new
                {
                    id = i.Id,
                    values = i.Vector,
                    metadata = i.Metadata
                }).ToArray()
            };
            await PostAsync("/vectors/upsert", body, ct);
        }

        public async Task<List<PineconeMatch>> QueryAsync(
            float[] vector,
            int topK,
            IDictionary<string, object>? filter = null,
            bool includeMetadata = true,
            bool includeValues = false,
            CancellationToken ct = default)
        {
            var body = new Dictionary<string, object?>
            {
                ["vector"] = vector,
                ["topK"] = topK,
                ["includeMetadata"] = includeMetadata,
                ["includeValues"] = includeValues
            };
            if (filter != null) body["filter"] = filter;

            string respJson = await PostAsync("/query", body, ct);
            using var doc = JsonDocument.Parse(respJson);

            var matches = new List<PineconeMatch>();
            if (!doc.RootElement.TryGetProperty("matches", out var matchesElem))
                return matches;

            foreach (var m in matchesElem.EnumerateArray())
            {
                string id = m.GetProperty("id").GetString()!;
                float score = m.TryGetProperty("score", out var s) ? s.GetSingle() : 0f;
                IDictionary<string, object>? md = null;
                if (m.TryGetProperty("metadata", out var mdElem))
                    md = ParseMetadata(mdElem);
                float[]? values = null;
                if (m.TryGetProperty("values", out var valuesElem) && valuesElem.ValueKind == JsonValueKind.Array)
                {
                    values = new float[valuesElem.GetArrayLength()];
                    int idx = 0;
                    foreach (var v in valuesElem.EnumerateArray())
                        values[idx++] = v.GetSingle();
                }
                matches.Add(new PineconeMatch(id, score, md, values));
            }
            return matches;
        }

        public async Task UpdateMetadataAsync(string id, IDictionary<string, object> metadata, CancellationToken ct = default)
        {
            var body = new { id, setMetadata = metadata };
            await PostAsync("/vectors/update", body, ct);
        }

        public Task DeleteAsync(string id, CancellationToken ct = default)
            => DeleteManyAsync(new[] { id }, ct);

        public async Task DeleteManyAsync(IEnumerable<string> ids, CancellationToken ct = default)
        {
            var idList = ids.ToList();
            if (idList.Count == 0) return;
            var body = new { ids = idList };
            await PostAsync("/vectors/delete", body, ct);
        }

        public async Task DeleteByFilterAsync(IDictionary<string, object> filter, CancellationToken ct = default)
        {
            var body = new { filter };
            await PostAsync("/vectors/delete", body, ct);
        }

        public async Task<List<PineconeVector>> FetchAsync(IEnumerable<string> ids, CancellationToken ct = default)
        {
            var idList = ids.ToList();
            if (idList.Count == 0) return new List<PineconeVector>();

            string qs = string.Join("&", idList.Select(id => $"ids={Uri.EscapeDataString(id)}"));
            var resp = await _http.GetAsync($"/vectors/fetch?{qs}", ct);
            string respJson = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogError("Pinecone fetch failed: {Status} {Body}", resp.StatusCode, respJson);
                throw new InvalidOperationException($"Pinecone fetch failed ({resp.StatusCode}): {respJson}");
            }

            var result = new List<PineconeVector>();
            using var doc = JsonDocument.Parse(respJson);
            if (!doc.RootElement.TryGetProperty("vectors", out var vectorsElem))
                return result;

            foreach (var prop in vectorsElem.EnumerateObject())
            {
                string id = prop.Value.GetProperty("id").GetString()!;
                float[] values = Array.Empty<float>();
                if (prop.Value.TryGetProperty("values", out var valuesElem))
                {
                    values = new float[valuesElem.GetArrayLength()];
                    int i = 0;
                    foreach (var v in valuesElem.EnumerateArray())
                        values[i++] = v.GetSingle();
                }
                IDictionary<string, object>? md = null;
                if (prop.Value.TryGetProperty("metadata", out var mdElem))
                    md = ParseMetadata(mdElem);
                result.Add(new PineconeVector(id, values, md));
            }
            return result;
        }

        private async Task<string> PostAsync(string path, object body, CancellationToken ct)
        {
            string json = JsonSerializer.Serialize(body, JsonOpts);
            using var req = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            var resp = await _http.SendAsync(req, ct);
            string respJson = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogError("Pinecone {Path} failed: {Status} {Body}", path, resp.StatusCode, respJson);
                throw new InvalidOperationException($"Pinecone {path} failed ({resp.StatusCode}): {respJson}");
            }
            return respJson;
        }

        private static IDictionary<string, object> ParseMetadata(JsonElement elem)
        {
            var d = new Dictionary<string, object>();
            foreach (var prop in elem.EnumerateObject())
            {
                object value = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString()!,
                    JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? (object)l : prop.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null!,
                    _ => prop.Value.GetRawText()
                };
                if (value != null) d[prop.Name] = value;
            }
            return d;
        }
    }
}
