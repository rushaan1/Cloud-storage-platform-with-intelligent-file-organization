using CloudStoragePlatform.Core.ServiceContracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CloudStoragePlatform.Core.Services.Ai
{
    public class GeminiCaptioner : IGeminiCaptioner
    {
        private readonly HttpClient _http;
        private readonly IVertexAccessTokenProvider _tokens;
        private readonly IConfiguration _config;
        private readonly ILogger<GeminiCaptioner> _logger;

        private const string CaptionPrompt =
            "Describe this image factually in 2-3 sentences, focusing on objects, text, and scene.";

        public GeminiCaptioner(HttpClient http, IVertexAccessTokenProvider tokens, IConfiguration config, ILogger<GeminiCaptioner> logger)
        {
            _http = http;
            _tokens = tokens;
            _config = config;
            _logger = logger;
        }

        public async Task<string> CaptionAsync(byte[] imageBytes, string mimeType, CancellationToken ct = default)
        {
            if (imageBytes == null || imageBytes.Length == 0) return string.Empty;

            string project = _config["Ai:Vertex:ProjectId"] ?? throw new InvalidOperationException("Ai:Vertex:ProjectId not configured.");
            string region = _config["Ai:Vertex:Region"] ?? throw new InvalidOperationException("Ai:Vertex:Region not configured.");
            string model = _config["Ai:Vertex:CaptionModel"] ?? "gemini-2.5-flash";
            string token = await _tokens.GetTokenAsync(ct);

            string url = $"https://{region}-aiplatform.googleapis.com/v1/projects/{project}/locations/{region}/publishers/google/models/{model}:generateContent";
            string b64 = Convert.ToBase64String(imageBytes);

            var body = new
            {
                contents = new[]
                {
                    new
                    {
                        role = "user",
                        parts = new object[]
                        {
                            new { inline_data = new { mime_type = mimeType, data = b64 } },
                            new { text = CaptionPrompt }
                        }
                    }
                }
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
                _logger.LogWarning("Gemini caption failed: {Status} {Body}", resp.StatusCode, respJson);
                return string.Empty;
            }

            try
            {
                using var doc = JsonDocument.Parse(respJson);
                var text = doc.RootElement
                    .GetProperty("candidates")[0]
                    .GetProperty("content")
                    .GetProperty("parts")[0]
                    .GetProperty("text")
                    .GetString();
                return text ?? string.Empty;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse Gemini caption response");
                return string.Empty;
            }
        }
    }
}
