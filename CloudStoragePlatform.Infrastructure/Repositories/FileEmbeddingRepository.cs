using CloudStoragePlatform.Core.Domain.Entities;
using CloudStoragePlatform.Core.Domain.RepositoryContracts;
using CloudStoragePlatform.Core.Enums;
using CloudStoragePlatform.Infrastructure.DbContext;
using Microsoft.EntityFrameworkCore;

namespace CloudStoragePlatform.Infrastructure.Repositories
{
    public class FileEmbeddingRepository : IFileEmbeddingRepository
    {
        private readonly ApplicationDbContext _db;

        public FileEmbeddingRepository(ApplicationDbContext db)
        {
            _db = db;
        }

        public async Task<FileEmbedding?> GetByFileId(Guid fileId)
            => await _db.FileEmbeddings.FirstOrDefaultAsync(e => e.FileId == fileId);

        public async Task<FileEmbedding> Add(FileEmbedding embedding)
        {
            _db.FileEmbeddings.Add(embedding);
            await _db.SaveChangesAsync();
            return embedding;
        }

        public async Task<FileEmbedding?> Update(FileEmbedding embedding)
        {
            var existing = await _db.FileEmbeddings.FirstOrDefaultAsync(e => e.Id == embedding.Id);
            if (existing == null) return null;
            _db.Entry(existing).CurrentValues.SetValues(embedding);
            await _db.SaveChangesAsync();
            return existing;
        }

        public async Task<bool> Delete(Guid fileId)
        {
            var existing = await _db.FileEmbeddings.FirstOrDefaultAsync(e => e.FileId == fileId);
            if (existing == null) return false;
            _db.FileEmbeddings.Remove(existing);
            return await _db.SaveChangesAsync() > 0;
        }

        public async Task<List<FileEmbedding>> GetByStatus(EmbeddingStatus status)
            => await _db.FileEmbeddings.Where(e => e.Status == status).ToListAsync();

        public async Task<List<FileEmbedding>> GetByUser(Guid userId)
            => await _db.FileEmbeddings.Where(e => e.UserId == userId).ToListAsync();

        public async Task<List<FileEmbedding>> GetMissingForUser(Guid userId)
        {
            var existingFileIds = _db.FileEmbeddings
                .Where(e => e.UserId == userId && e.Status == EmbeddingStatus.Completed)
                .Select(e => e.FileId);

            var missingFiles = await _db.Files
                .Where(f => f.UserId == userId && !existingFileIds.Contains(f.FileId))
                .Select(f => new FileEmbedding
                {
                    Id = Guid.NewGuid(),
                    FileId = f.FileId,
                    UserId = userId,
                    Status = EmbeddingStatus.Pending
                })
                .ToListAsync();

            return missingFiles;
        }

        public async Task<int> ResetStuckProcessing()
        {
            var stuck = await _db.FileEmbeddings
                .Where(e => e.Status == EmbeddingStatus.Processing)
                .ToListAsync();
            foreach (var e in stuck) e.Status = EmbeddingStatus.Pending;
            await _db.SaveChangesAsync();
            return stuck.Count;
        }
    }
}
