using CloudStoragePlatform.Core.ServiceContracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CloudStoragePlatform.Core.Services.Ai
{
    public class GeminiTagger : IGeminiTagger
    {
        private readonly HttpClient _http;
        private readonly IVertexAccessTokenProvider _tokens;
        private readonly IConfiguration _config;
        private readonly ILogger<GeminiTagger> _logger;

        private const string TagPrompt =
            "Generate 2 to 5 short topical tags for the file described below. " +
            "Each tag MUST be 1 to 4 words. Prefer concrete nouns and topic descriptors. " +
            "Reply with ONLY a JSON array of strings, no surrounding prose, no markdown, no code fences. " +
            "Example output: [\"spring flowers\", \"garden photo\", \"outdoor\"].";

        public GeminiTagger(HttpClient http, IVertexAccessTokenProvider tokens, IConfiguration config, ILogger<GeminiTagger> logger)
        {
            _http = http;
            _tokens = tokens;
            _config = config;
            _logger = logger;
        }

        public async Task<List<string>> TagAsync(string extractedText, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(extractedText)) return new List<string>();

            string project = _config["Ai:Vertex:ProjectId"] ?? throw new InvalidOperationException("Ai:Vertex:ProjectId not configured.");
            string region = _config["Ai:Vertex:Region"] ?? throw new InvalidOperationException("Ai:Vertex:Region not configured.");
            string model = _config["Ai:Vertex:CaptionModel"] ?? "gemini-2.5-flash";
            string token = await _tokens.GetTokenAsync(ct);

            string url = $"https://{region}-aiplatform.googleapis.com/v1/projects/{project}/locations/{region}/publishers/google/models/{model}:generateContent";

            var body = new
            {
                contents = new[]
                {
                    new
                    {
                        role = "user",
                        parts = new object[]
                        {
                            new { text = TagPrompt },
                            new { text = "FILE:\n" + extractedText }
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
                _logger.LogWarning("Gemini tagger failed: {Status} {Body}", resp.StatusCode, respJson);
                return new List<string>();
            }

            string text;
            try
            {
                using var doc = JsonDocument.Parse(respJson);
                text = doc.RootElement
                    .GetProperty("candidates")[0]
                    .GetProperty("content")
                    .GetProperty("parts")[0]
                    .GetProperty("text")
                    .GetString() ?? string.Empty;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse Gemini tagger response");
                return new List<string>();
            }

            // Strip code fences in case Gemini wrapped the JSON despite the prompt.
            text = text.Trim();
            if (text.StartsWith("```"))
            {
                int firstNl = text.IndexOf('\n');
                if (firstNl >= 0) text = text.Substring(firstNl + 1);
                int closingFence = text.LastIndexOf("```");
                if (closingFence >= 0) text = text.Substring(0, closingFence);
                text = text.Trim();
            }

            try
            {
                var tags = JsonSerializer.Deserialize<List<string>>(text);
                if (tags == null) return new List<string>();
                return tags
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Select(CleanTag)
                    .Where(t => t.Length > 0)
                    .Take(5)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse tags JSON: {Text}", text);
                return new List<string>();
            }
        }

        private static string CleanTag(string t)
        {
            t = t.Trim().Trim(',').Trim('"').Trim();
            // Enforce max 4 words.
            var words = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 4) t = string.Join(' ', words.Take(4));
            // Length cap per tag (defense against runaway content).
            if (t.Length > 40) t = t.Substring(0, 40).TrimEnd();
            return t;
        }
    }
}
