using CloudStoragePlatform.Core.Domain.Entities;
using CloudStoragePlatform.Core.Domain.RepositoryContracts;
using CloudStoragePlatform.Core.Enums;
using CloudStoragePlatform.Core.ServiceContracts;
using Microsoft.Extensions.Logging;

namespace CloudStoragePlatform.Core.Services.Ai
{
    public class FileEmbeddingSync : IFileEmbeddingSync
    {
        private readonly IEmbeddingOrchestrator _orchestrator;
        private readonly IPineconeClient _pinecone;
        private readonly IFileEmbeddingRepository _embRepo;
        private readonly IFolderEmbeddingRepository _folderEmbRepo;
        private readonly IFoldersRepository _foldersRepo;
        private readonly ILogger<FileEmbeddingSync> _logger;

        public FileEmbeddingSync(
            IEmbeddingOrchestrator orchestrator,
            IPineconeClient pinecone,
            IFileEmbeddingRepository embRepo,
            IFolderEmbeddingRepository folderEmbRepo,
            IFoldersRepository foldersRepo,
            ILogger<FileEmbeddingSync> logger)
        {
            _orchestrator = orchestrator;
            _pinecone = pinecone;
            _embRepo = embRepo;
            _folderEmbRepo = folderEmbRepo;
            _foldersRepo = foldersRepo;
            _logger = logger;
        }

        public async ValueTask EnqueueOnCreate(Guid fileId, Guid userId, CancellationToken ct = default)
        {
            try { await _orchestrator.EnqueueAsync(new EmbeddingJob(fileId, userId, EmbeddingReason.Created), ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "EnqueueOnCreate failed for FileId={FileId}", fileId); }
        }

        public async ValueTask EnqueueOnRename(Guid fileId, Guid userId, CancellationToken ct = default)
        {
            try { await _orchestrator.EnqueueAsync(new EmbeddingJob(fileId, userId, EmbeddingReason.Renamed), ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "EnqueueOnRename failed for FileId={FileId}", fileId); }
        }

        public async Task UpdateMetadataOnMove(Guid fileId, Guid newFolderId, string newFolderPath, Guid oldFolderId, Guid userId, CancellationToken ct = default)
        {
            try
            {
                await _pinecone.UpdateMetadataAsync(fileId.ToString(), new Dictionary<string, object>
                {
                    ["folderId"] = newFolderId.ToString(),
                    ["folderPath"] = newFolderPath
                }, ct);
                await _folderEmbRepo.MarkStale(oldFolderId);
                await _folderEmbRepo.Upsert(newFolderId, userId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "UpdateMetadataOnMove failed for FileId={FileId}", fileId);
            }
        }

        public async Task UpdateMetadataOnTrash(Guid fileId, bool isTrash, Guid parentFolderId, Guid userId, CancellationToken ct = default)
        {
            try
            {
                await _pinecone.UpdateMetadataAsync(fileId.ToString(), new Dictionary<string, object>
                {
                    ["isTrash"] = isTrash
                }, ct);
                await _folderEmbRepo.MarkStale(parentFolderId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "UpdateMetadataOnTrash failed for FileId={FileId}", fileId);
            }
        }

        public async Task DeleteOnHardDelete(Guid fileId, Guid parentFolderId, Guid userId, CancellationToken ct = default)
        {
            try
            {
                await _pinecone.DeleteAsync(fileId.ToString(), ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Pinecone delete failed for FileId={FileId}", fileId);
            }
            try { await _embRepo.Delete(fileId); } catch (Exception ex) { _logger.LogWarning(ex, "Embedding row delete failed for FileId={FileId}", fileId); }
            try { await _folderEmbRepo.MarkStale(parentFolderId); } catch (Exception ex) { _logger.LogWarning(ex, "MarkStale failed for FolderId={FolderId}", parentFolderId); }
        }

        public async Task CascadeFolderTrash(Guid folderId, bool isTrash, Guid userId, CancellationToken ct = default)
        {
            try
            {
                var fileIds = await CollectDescendantFileIds(folderId);
                foreach (var fid in fileIds)
                {
                    try
                    {
                        await _pinecone.UpdateMetadataAsync(fid.ToString(), new Dictionary<string, object>
                        {
                            ["isTrash"] = isTrash
                        }, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "CascadeFolderTrash failed for descendant FileId={FileId}", fid);
                    }
                }
                await _folderEmbRepo.MarkStale(folderId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CascadeFolderTrash failed for FolderId={FolderId}", folderId);
            }
        }

        public async Task CascadeFolderDelete(Guid folderId, Guid userId, CancellationToken ct = default)
        {
            try
            {
                var fileIds = await CollectDescendantFileIds(folderId);
                if (fileIds.Count > 0)
                {
                    try
                    {
                        await _pinecone.DeleteManyAsync(fileIds.Select(f => f.ToString()), ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Pinecone batch delete failed for cascade FolderId={FolderId}", folderId);
                    }
                }
                foreach (var fid in fileIds)
                {
                    try { await _embRepo.Delete(fid); } catch { /* ignore */ }
                }

                var folderIds = await CollectDescendantFolderIds(folderId);
                foreach (var f in folderIds)
                {
                    try { await _pinecone.DeleteAsync($"folder_{f}", ct); } catch { /* may not exist */ }
                    try { await _folderEmbRepo.Delete(f); } catch { /* ignore */ }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CascadeFolderDelete failed for FolderId={FolderId}", folderId);
            }
        }

        private async Task<List<Guid>> CollectDescendantFileIds(Guid folderId)
        {
            var result = new List<Guid>();
            var folder = await _foldersRepo.GetFolderByFolderId(folderId);
            if (folder == null) return result;
            CollectFilesRecursive(folder, result);
            return result;
        }

        private void CollectFilesRecursive(Folder folder, List<Guid> accumulator)
        {
            foreach (var file in folder.Files) accumulator.Add(file.FileId);
            foreach (var sub in folder.SubFolders)
                CollectFilesRecursive(sub, accumulator);
        }

        private async Task<List<Guid>> CollectDescendantFolderIds(Guid folderId)
        {
            var result = new List<Guid> { folderId };
            var folder = await _foldersRepo.GetFolderByFolderId(folderId);
            if (folder == null) return result;
            CollectFoldersRecursive(folder, result);
            return result;
        }

        private void CollectFoldersRecursive(Folder folder, List<Guid> accumulator)
        {
            foreach (var sub in folder.SubFolders)
            {
                accumulator.Add(sub.FolderId);
                CollectFoldersRecursive(sub, accumulator);
            }
        }
    }
}
