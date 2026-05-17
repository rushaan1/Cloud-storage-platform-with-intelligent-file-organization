using CloudStoragePlatform.Core.Domain.Entities;
using CloudStoragePlatform.Core.Domain.IdentityEntites;
using CloudStoragePlatform.Core.Domain.RepositoryContracts;
using CloudStoragePlatform.Core.Enums;
using CloudStoragePlatform.Core.ServiceContracts;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using File = CloudStoragePlatform.Core.Domain.Entities.File;

namespace CloudStoragePlatform.Core.Services.Ai
{
    /// <summary>
    /// Singleton hosted service that owns an internal queue of embedding jobs and processes them off-thread.
    /// Per-job: creates a DI scope, sets user context, extracts content, embeds via Vertex, upserts to Pinecone.
    /// Wraps each job in MinRamThreshold and retries with exponential backoff.
    /// </summary>
    public class EmbeddingOrchestrator : BackgroundService, IEmbeddingOrchestrator
    {
        private readonly Channel<EmbeddingJob> _channel;
        private readonly IServiceProvider _provider;
        private readonly IConfiguration _config;
        private readonly ILogger<EmbeddingOrchestrator> _logger;
        private readonly SemaphoreSlim _concurrency;
        private readonly int _maxRetries;

        public EmbeddingOrchestrator(IServiceProvider provider, IConfiguration config, ILogger<EmbeddingOrchestrator> logger)
        {
            _provider = provider;
            _config = config;
            _logger = logger;

            int capacity = int.TryParse(_config["Ai:Embedding:QueueCapacity"], out var c) ? c : 1024;
            int maxParallel = int.TryParse(_config["Ai:Embedding:MaxParallel"], out var p) ? p : 2;
            _maxRetries = int.TryParse(_config["Ai:Embedding:MaxRetries"], out var r) ? r : 3;

            _channel = Channel.CreateBounded<EmbeddingJob>(new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = false,
                SingleWriter = false
            });
            _concurrency = new SemaphoreSlim(maxParallel, maxParallel);
        }

        public int PendingCount => _channel.Reader.Count;

        public async ValueTask EnqueueAsync(EmbeddingJob job, CancellationToken ct = default)
        {
            await _channel.Writer.WriteAsync(job, ct);
        }

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var scope = _provider.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<IFileEmbeddingRepository>();
                int reset = await repo.ResetStuckProcessing();
                if (reset > 0)
                    _logger.LogInformation("Reset {Count} stuck embedding rows back to Pending", reset);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not reset stuck Processing rows on startup");
            }

            await base.StartAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Embedding orchestrator started");
            try
            {
                await foreach (var job in _channel.Reader.ReadAllAsync(stoppingToken))
                {
                    await _concurrency.WaitAsync(stoppingToken);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await MinRamThreshold.WaitForRamOrTimeoutAsync(() => ProcessJobAsync(job, stoppingToken));
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Unhandled error in embedding job FileId={FileId}", job.FileId);
                        }
                        finally
                        {
                            _concurrency.Release();
                        }
                    }, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Embedding orchestrator stopping (cancelled)");
            }
        }

        private async Task ProcessJobAsync(EmbeddingJob job, CancellationToken ct)
        {
            using var scope = _provider.CreateScope();
            var sp = scope.ServiceProvider;
            var fileRepo = sp.GetRequiredService<IFilesRepository>();
            var embRepo = sp.GetRequiredService<IFileEmbeddingRepository>();
            var folderEmbRepo = sp.GetRequiredService<IFolderEmbeddingRepository>();
            var extractor = sp.GetRequiredService<IContentExtractor>();
            var vertex = sp.GetRequiredService<IVertexEmbeddingClient>();
            var pinecone = sp.GetRequiredService<IPineconeClient>();
            var sse = sp.GetRequiredService<SSE>();
            var ui = sp.GetRequiredService<UserIdentification>();
            var userManager = sp.GetRequiredService<UserManager<ApplicationUser>>();

            // Set user context for this scope (required by repositories)
            ApplicationUser? user = await userManager.FindByIdAsync(job.UserId.ToString());
            if (user == null)
            {
                _logger.LogWarning("Embedding job for unknown UserId={UserId}; dropping", job.UserId);
                return;
            }
            ui.User = user;

            File? file = await fileRepo.GetFileByFileId(job.FileId);
            if (file == null)
            {
                _logger.LogWarning("Embedding job for missing FileId={FileId}; dropping", job.FileId);
                // Also remove the orphan row if any
                await embRepo.Delete(job.FileId);
                return;
            }

            // Ensure FileEmbedding row exists
            FileEmbedding? existing = await embRepo.GetByFileId(job.FileId);
            FileEmbedding emb;
            if (existing == null)
            {
                emb = new FileEmbedding
                {
                    Id = Guid.NewGuid(),
                    FileId = job.FileId,
                    UserId = job.UserId,
                    Status = EmbeddingStatus.Pending,
                    AttemptCount = 0
                };
                emb = await embRepo.Add(emb);
            }
            else
            {
                emb = existing;
            }

            try
            {
                string text = await extractor.ExtractAsync(file, ct);
                string hash = ComputeHash(text);

                // Idempotency: skip if content unchanged and already completed
                if (existing != null
                    && existing.Status == EmbeddingStatus.Completed
                    && existing.ContentHash == hash
                    && !string.IsNullOrEmpty(existing.VectorId))
                {
                    _logger.LogDebug("Content unchanged for FileId={FileId}; no re-embed", file.FileId);
                    return;
                }

                emb.Status = EmbeddingStatus.Processing;
                emb.AttemptCount++;
                await embRepo.Update(emb);

                float[] vector = await vertex.EmbedAsync(text, EmbeddingTaskType.RetrievalDocument, ct);

                var metadata = BuildFileMetadata(file, user.Id);
                await pinecone.UpsertAsync(file.FileId.ToString(), vector, metadata, ct);

                emb.Status = EmbeddingStatus.Completed;
                emb.VectorId = file.FileId.ToString();
                emb.ContentHash = hash;
                emb.EmbeddedAt = DateTime.UtcNow;
                emb.ErrorMessage = null;
                await embRepo.Update(emb);

                // Mark parent folder centroid stale (Upsert also creates if missing)
                await folderEmbRepo.Upsert(file.ParentFolderId, user.Id);

                await sse.SendEventAsync("embedded", new { fileId = file.FileId }, user.Id);
                _logger.LogInformation("Embedded FileId={FileId} (reason={Reason}, attempt={Attempt})", file.FileId, job.Reason, emb.AttemptCount);

                try
                {
                    var suggestionSvc = sp.GetRequiredService<IFolderSuggestionService>();
                    var suggestions = await suggestionSvc.SuggestFoldersForVectorAsync(vector, file.ParentFolderId, user.Id, 3, ct);
                    if (suggestions.Count > 0)
                    {
                        await sse.SendEventAsync("folder_suggestion", new
                        {
                            fileId = file.FileId,
                            fileName = file.FileName,
                            suggestions = suggestions.Select(s => new { folderId = s.FolderId, folderPath = s.FolderPath, folderName = s.FolderName, score = s.Score }).ToList()
                        }, user.Id);
                        _logger.LogInformation("Sent folder_suggestion for FileId={FileId} with {Count} candidates", file.FileId, suggestions.Count);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Folder suggestion failed for FileId={FileId}", file.FileId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Embedding failed for FileId={FileId}, attempt {Attempt}/{Max}",
                    file.FileId, emb.AttemptCount, _maxRetries);

                emb.Status = emb.AttemptCount >= _maxRetries ? EmbeddingStatus.Failed : EmbeddingStatus.Pending;
                emb.ErrorMessage = ex.Message;
                await embRepo.Update(emb);

                if (emb.Status == EmbeddingStatus.Pending)
                {
                    int delaySeconds = (int)Math.Pow(4, Math.Max(0, emb.AttemptCount - 1));
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct);
                            await EnqueueAsync(job, ct);
                        }
                        catch (OperationCanceledException) { }
                    }, ct);
                }
            }
        }

        private static IDictionary<string, object> BuildFileMetadata(File file, Guid userId)
        {
            return new Dictionary<string, object>
            {
                ["userId"] = userId.ToString(),
                ["type"] = "file",
                ["fileId"] = file.FileId.ToString(),
                ["folderId"] = file.ParentFolderId.ToString(),
                ["folderPath"] = file.ParentFolder?.FolderPath ?? string.Empty,
                ["fileName"] = file.FileName,
                ["fileType"] = (int)file.FileType,
                ["isTrash"] = file.IsTrash,
                ["embeddedAt"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };
        }

        private static string ComputeHash(string text)
        {
            using var sha = SHA256.Create();
            byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
            return Convert.ToHexString(bytes);
        }
    }
}
