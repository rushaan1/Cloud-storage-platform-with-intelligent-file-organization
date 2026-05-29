using CloudStoragePlatform.Core.Domain.Entities;
using CloudStoragePlatform.Core.Enums;

namespace CloudStoragePlatform.Core.Domain.RepositoryContracts
{
    public interface IFileEmbeddingRepository
    {
        Task<FileEmbedding?> GetByFileId(Guid fileId);
        Task<FileEmbedding> Add(FileEmbedding embedding);
        Task<FileEmbedding?> Update(FileEmbedding embedding);
        Task<bool> Delete(Guid fileId);
        Task<List<FileEmbedding>> GetByStatus(EmbeddingStatus status);
        Task<List<FileEmbedding>> GetByUser(Guid userId);
        Task<List<FileEmbedding>> GetMissingForUser(Guid userId);
        Task<int> ResetStuckProcessing();
    }
}
