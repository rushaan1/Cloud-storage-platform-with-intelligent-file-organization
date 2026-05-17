using CloudStoragePlatform.Core.Domain.Entities;
using CloudStoragePlatform.Core.Domain.RepositoryContracts;
using CloudStoragePlatform.Infrastructure.DbContext;
using Microsoft.EntityFrameworkCore;

namespace CloudStoragePlatform.Infrastructure.Repositories
{
    public class FolderEmbeddingRepository : IFolderEmbeddingRepository
    {
        private readonly ApplicationDbContext _db;

        public FolderEmbeddingRepository(ApplicationDbContext db)
        {
            _db = db;
        }

        public async Task<FolderEmbedding?> GetByFolderId(Guid folderId)
            => await _db.FolderEmbeddings.FirstOrDefaultAsync(e => e.FolderId == folderId);

        public async Task<FolderEmbedding> Add(FolderEmbedding embedding)
        {
            _db.FolderEmbeddings.Add(embedding);
            await _db.SaveChangesAsync();
            return embedding;
        }

        public async Task<FolderEmbedding?> Update(FolderEmbedding embedding)
        {
            var existing = await _db.FolderEmbeddings.FirstOrDefaultAsync(e => e.Id == embedding.Id);
            if (existing == null) return null;
            _db.Entry(existing).CurrentValues.SetValues(embedding);
            await _db.SaveChangesAsync();
            return existing;
        }

        public async Task<bool> Delete(Guid folderId)
        {
            var existing = await _db.FolderEmbeddings.FirstOrDefaultAsync(e => e.FolderId == folderId);
            if (existing == null) return false;
            _db.FolderEmbeddings.Remove(existing);
            return await _db.SaveChangesAsync() > 0;
        }

        public async Task<List<FolderEmbedding>> GetStaleForUser(Guid userId)
            => await _db.FolderEmbeddings.Where(e => e.UserId == userId && e.IsStale).ToListAsync();

        public async Task<List<FolderEmbedding>> GetAllStale()
            => await _db.FolderEmbeddings.Where(e => e.IsStale).ToListAsync();

        public async Task<FolderEmbedding> Upsert(Guid folderId, Guid userId)
        {
            var existing = await _db.FolderEmbeddings.FirstOrDefaultAsync(e => e.FolderId == folderId);
            if (existing != null)
            {
                existing.IsStale = true;
                await _db.SaveChangesAsync();
                return existing;
            }

            var entity = new FolderEmbedding
            {
                Id = Guid.NewGuid(),
                FolderId = folderId,
                UserId = userId,
                VectorId = $"folder_{folderId}",
                IsStale = true
            };
            _db.FolderEmbeddings.Add(entity);
            await _db.SaveChangesAsync();
            return entity;
        }

        public async Task MarkStale(Guid folderId)
        {
            var existing = await _db.FolderEmbeddings.FirstOrDefaultAsync(e => e.FolderId == folderId);
            if (existing != null && !existing.IsStale)
            {
                existing.IsStale = true;
                await _db.SaveChangesAsync();
            }
        }
    }
}
