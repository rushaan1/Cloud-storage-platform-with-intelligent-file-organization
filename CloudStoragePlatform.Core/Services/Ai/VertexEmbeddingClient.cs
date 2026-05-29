using CloudStoragePlatform.Core.Enums;
using CloudStoragePlatform.Core.ServiceContracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CloudStoragePlatform.Core.Services.Ai
{
    public class VertexEmbeddingClient : IVertexEmbeddingClient
    {
        private readonly HttpClient _http;
        private readonly IVertexAccessTokenProvider _tokens;
        private readonly IConfiguration _config;
        private readonly ILogger<VertexEmbeddingClient> _logger;

        public VertexEmbeddingClient(HttpClient http, IVertexAccessTokenProvider tokens, IConfiguration config, ILogger<VertexEmbeddingClient> logger)
        {
            _http = http;
            _tokens = tokens;
            _config = config;
            _logger = logger;
        }

        public async Task<float[]> EmbedAsync(string text, EmbeddingTaskType taskType, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("text is empty", nameof(text));

            string project = _config["Ai:Vertex:ProjectId"] ?? throw new InvalidOperationException("Ai:Vertex:ProjectId not configured.");
            string region = _config["Ai:Vertex:Region"] ?? throw new InvalidOperationException("Ai:Vertex:Region not configured.");
            string model = _config["Ai:Vertex:EmbeddingModel"] ?? "text-embedding-005";
            string token = await _tokens.GetTokenAsync(ct);

            string url = $"https://{region}-aiplatform.googleapis.com/v1/projects/{project}/locations/{region}/publishers/google/models/{model}:predict";
            string taskTypeStr = taskType == EmbeddingTaskType.RetrievalQuery ? "RETRIEVAL_QUERY" : "RETRIEVAL_DOCUMENT";

            var body = new
            {
                instances = new[] { new { content = text, task_type = taskTypeStr } }
            };
            string json = JsonSerializer.Serialize(body);

            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var resp = await _http.SendAsync(req, ct);
            string respJson = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogError("Vertex embedding failed: {Status} {Body}", resp.StatusCode, respJson);
                throw new InvalidOperationException($"Vertex embedding failed ({resp.StatusCode}): {respJson}");
            }

            using var doc = JsonDocument.Parse(respJson);
            var values = doc.RootElement
                .GetProperty("predictions")[0]
                .GetProperty("embeddings")
                .GetProperty("values");

            var arr = new float[values.GetArrayLength()];
            int i = 0;
            foreach (var v in values.EnumerateArray())
                arr[i++] = v.GetSingle();
            return arr;
        }
    }
}
