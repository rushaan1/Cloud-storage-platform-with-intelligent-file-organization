using CloudStoragePlatform.Core.Domain.IdentityEntites;
using CloudStoragePlatform.Core.Domain.RepositoryContracts;
using CloudStoragePlatform.Core.Enums;
using CloudStoragePlatform.Core.ServiceContracts;
using Microsoft.AspNetCore.Identity;
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
        private readonly float _nameWeight;

        public FolderCentroidRecomputer(IServiceProvider provider, IConfiguration config, ILogger<FolderCentroidRecomputer> logger)
        {
            _provider = provider;
            _logger = logger;
            int seconds = int.TryParse(config["Ai:FolderCentroid:RecomputeIntervalSeconds"], out var s) ? s : 300;
            _interval = TimeSpan.FromSeconds(seconds);
            _dimension = int.TryParse(config["Ai:Pinecone:Dimension"], out var d) ? d : 768;
            _nameWeight = float.TryParse(config["Ai:FolderCentroid:NameWeight"], out var nw) ? nw : 0.3f;
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
            var foldersRepo = sp.GetRequiredService<IFoldersRepository>();
            var vertex = sp.GetRequiredService<IVertexEmbeddingClient>();
            var ui = sp.GetRequiredService<UserIdentification>();
            var userManager = sp.GetRequiredService<UserManager<ApplicationUser>>();

            var stale = await folderEmbRepo.GetAllStale();
            if (stale.Count == 0) return;
            _logger.LogDebug("Recomputing {Count} stale folder centroids", stale.Count);

            // Non-zero query vector — cosine query is undefined for zero. Values returned are independent of the query vector when topK is large enough.
            float[] probe = new float[_dimension];
            for (int i = 0; i < _dimension; i++) probe[i] = 1f;

            var userCache = new Dictionary<Guid, ApplicationUser?>();

            foreach (var fe in stale)
            {
                try
                {
                    // Resolve + set the owning user so the user-scoped folders repo works inside this background pass.
                    if (!userCache.TryGetValue(fe.UserId, out var owner))
                    {
                        owner = await userManager.FindByIdAsync(fe.UserId.ToString());
                        userCache[fe.UserId] = owner;
                    }
                    if (owner == null) continue;
                    ui.User = owner;

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

                    // Component-wise mean of the folder's file vectors.
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

                    // Blend in the meaning of the folder's OWN name so the centroid values the name,
                    // not just the contents of the files currently inside it.
                    if (_nameWeight > 0f)
                    {
                        try
                        {
                            var folder = await foldersRepo.GetFolderByFolderId(fe.FolderId);
                            if (folder != null && !string.IsNullOrWhiteSpace(folder.FolderName))
                            {
                                float[] nameVec = await vertex.EmbedAsync($"Folder name: {folder.FolderName}", EmbeddingTaskType.RetrievalDocument, ct);
                                centroid = Blend(centroid, nameVec, _nameWeight);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Folder-name blend failed for FolderId={FolderId}; using file-only centroid", fe.FolderId);
                        }
                    }

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

        // Convex blend of two vectors after L2-normalizing each, so the weight controls the mix
        // independent of raw magnitudes. Result is fed to Pinecone (cosine), so it need not be unit length.
        private static float[] Blend(float[] fileMean, float[] nameVec, float nameWeight)
        {
            int dim = Math.Min(fileMean.Length, nameVec.Length);
            float[] a = Normalize(fileMean, dim);
            float[] b = Normalize(nameVec, dim);
            float[] result = new float[dim];
            for (int i = 0; i < dim; i++)
                result[i] = (1f - nameWeight) * a[i] + nameWeight * b[i];
            return result;
        }

        private static float[] Normalize(float[] v, int dim)
        {
            double sumSq = 0;
            for (int i = 0; i < dim; i++) sumSq += (double)v[i] * v[i];
            float norm = (float)Math.Sqrt(sumSq);
            if (norm <= 1e-8f) return v.Take(dim).ToArray();
            float[] r = new float[dim];
            for (int i = 0; i < dim; i++) r[i] = v[i] / norm;
            return r;
        }
    }
}
