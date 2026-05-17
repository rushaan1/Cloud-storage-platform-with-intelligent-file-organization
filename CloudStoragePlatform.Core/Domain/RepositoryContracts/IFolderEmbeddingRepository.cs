using CloudStoragePlatform.Core.Domain.Entities;

namespace CloudStoragePlatform.Core.Domain.RepositoryContracts
{
    public interface IFolderEmbeddingRepository
    {
        Task<FolderEmbedding?> GetByFolderId(Guid folderId);
        Task<FolderEmbedding> Add(FolderEmbedding embedding);
        Task<FolderEmbedding?> Update(FolderEmbedding embedding);
        Task<bool> Delete(Guid folderId);
        Task<List<FolderEmbedding>> GetStaleForUser(Guid userId);
        Task<List<FolderEmbedding>> GetAllStale();
        Task<FolderEmbedding> Upsert(Guid folderId, Guid userId);
        Task MarkStale(Guid folderId);
    }
}
