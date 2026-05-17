using CloudStoragePlatform.Core.Domain.RepositoryContracts;
using CloudStoragePlatform.Core.ServiceContracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CloudStoragePlatform.Core.Services.Ai
{
    /// <summary>
    /// Periodically rebuilds folder centroid vectors from their child file vectors.
    /// Centroid = component-wise mean of all file vectors in the folder; upserted to Pinecone with type=folder metadata.
    /// </summary>
    public class FolderCentroidRecomputer : BackgroundService
    {
        private readonly IServiceProvider _provider;
        private readonly ILogger<FolderCentroidRecomputer> _logger;
        private readonly TimeSpan _interval;
        private readonly int _dimension;

        public FolderCentroidRecomputer(IServiceProvider provider, IConfiguration config, ILogger<FolderCentroidRecomputer> logger)
        {
            _provider = provider;
            _logger = logger;
            int seconds = int.TryParse(config["Ai:FolderCentroid:RecomputeIntervalSeconds"], out var s) ? s : 300;
            _interval = TimeSpan.FromSeconds(seconds);
            _dimension = int.TryParse(config["Ai:Pinecone:Dimension"], out var d) ? d : 768;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Folder centroid recomputer started (interval {Seconds}s)", (int)_interval.TotalSeconds);
            try { await Task.Delay(_interval, stoppingToken); } catch (OperationCanceledException) { return; }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Folder centroid recompute pass failed");
                }
                try { await Task.Delay(_interval, stoppingToken); } catch (OperationCanceledException) { return; }
            }
        }

        public async Task RunOnceAsync(CancellationToken ct)
        {
            using var scope = _provider.CreateScope();
            var sp = scope.ServiceProvider;
            var folderEmbRepo = sp.GetRequiredService<IFolderEmbeddingRepository>();
            var pinecone = sp.GetRequiredService<IPineconeClient>();

            var stale = await folderEmbRepo.GetAllStale();
            if (stale.Count == 0) return;
            _logger.LogDebug("Recomputing {Count} stale folder centroids", stale.Count);

            // Non-zero query vector — cosine query is undefined for zero. Values returned are independent of the query vector when topK is large enough.
            float[] probe = new float[_dimension];
            for (int i = 0; i < _dimension; i++) probe[i] = 1f;

            foreach (var fe in stale)
            {
                try
                {
                    var filter = new Dictionary<string, object>
                    {
                        ["userId"] = fe.UserId.ToString(),
                        ["folderId"] = fe.FolderId.ToString(),
                        ["type"] = "file",
                        ["isTrash"] = false
                    };

                    var matches = await pinecone.QueryAsync(
                        vector: probe,
                        topK: 10000,
                        filter: filter,
                        includeMetadata: false,
                        includeValues: true,
                        ct: ct);

                    if (matches.Count == 0)
                    {
                        try { await pinecone.DeleteAsync(fe.VectorId, ct); } catch { /* may not exist yet */ }
                        fe.IsStale = false;
                        fe.FileCount = 0;
                        fe.LastComputedAt = DateTime.UtcNow;
                        await folderEmbRepo.Update(fe);
                        continue;
                    }

                    // Component-wise mean
                    int dim = matches.First(m => m.Values != null).Values!.Length;
                    float[] centroid = new float[dim];
                    int counted = 0;
                    foreach (var m in matches)
                    {
                        if (m.Values == null || m.Values.Length != dim) continue;
                        for (int i = 0; i < dim; i++) centroid[i] += m.Values[i];
                        counted++;
                    }
                    if (counted == 0)
                    {
                        _logger.LogWarning("Folder {FolderId}: no values returned despite {MatchCount} matches", fe.FolderId, matches.Count);
                        continue;
                    }
                    for (int i = 0; i < dim; i++) centroid[i] /= counted;

                    var metadata = new Dictionary<string, object>
                    {
                        ["userId"] = fe.UserId.ToString(),
                        ["type"] = "folder",
                        ["folderId"] = fe.FolderId.ToString(),
                        ["fileCount"] = counted,
                        ["computedAt"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    };
                    await pinecone.UpsertAsync(fe.VectorId, centroid, metadata, ct);

                    fe.IsStale = false;
                    fe.FileCount = counted;
                    fe.LastComputedAt = DateTime.UtcNow;
                    await folderEmbRepo.Update(fe);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to recompute centroid for FolderId={FolderId}", fe.FolderId);
                }
            }
        }
    }
}
