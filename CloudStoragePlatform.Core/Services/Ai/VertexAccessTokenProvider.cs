using CloudStoragePlatform.Core.ServiceContracts;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CloudStoragePlatform.Core.Services.Ai
{
    /// <summary>
    /// Caches a Vertex AI OAuth access token (~1h TTL) so callers don't repeat the JWT/RSA bootstrap on every request.
    /// </summary>
    public class VertexAccessTokenProvider : IVertexAccessTokenProvider
    {
        private readonly IConfiguration _config;
        private readonly ILogger<VertexAccessTokenProvider> _logger;
        private readonly SemaphoreSlim _lock = new(1, 1);

        private string? _cachedToken;
        private DateTime _expiresAtUtc;

        public VertexAccessTokenProvider(IConfiguration config, ILogger<VertexAccessTokenProvider> logger)
        {
            _config = config;
            _logger = logger;
        }

        public async Task<string> GetTokenAsync(CancellationToken ct = default)
        {
            if (_cachedToken != null && DateTime.UtcNow < _expiresAtUtc)
                return _cachedToken;

            await _lock.WaitAsync(ct);
            try
            {
                if (_cachedToken != null && DateTime.UtcNow < _expiresAtUtc)
                    return _cachedToken;

                string? base64 = _config["GoogleServiceAccountJsonKey"];
                if (string.IsNullOrWhiteSpace(base64))
                    throw new InvalidOperationException("GoogleServiceAccountJsonKey is not configured.");

                string saJson = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
                using var doc = JsonDocument.Parse(saJson);
                var root = doc.RootElement;
                string clientEmail = root.GetProperty("client_email").GetString()!;
                string privateKeyPem = root.GetProperty("private_key").GetString()!;

                RSA rsa = RSA.Create();
                rsa.ImportFromPem(privateKeyPem.ToCharArray());

                var initializer = new ServiceAccountCredential.Initializer(clientEmail)
                {
                    Scopes = new[] { "https://www.googleapis.com/auth/cloud-platform" },
                    Key = rsa
                };

                var svcCred = new ServiceAccountCredential(initializer);
                bool success = await svcCred.RequestAccessTokenAsync(ct);
                if (!success) throw new InvalidOperationException("Failed to obtain Google access token.");

                _cachedToken = svcCred.Token.AccessToken;
                _expiresAtUtc = DateTime.UtcNow.AddMinutes(50);
                _logger.LogDebug("Refreshed Vertex AI access token; valid until {ExpiresAt}", _expiresAtUtc);
                return _cachedToken!;
            }
            finally
            {
                _lock.Release();
            }
        }
    }
}
