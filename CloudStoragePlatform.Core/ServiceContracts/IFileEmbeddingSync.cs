namespace CloudStoragePlatform.Core.ServiceContracts
{
    /// <summary>
    /// Thin facade used by Modification services to keep Pinecone + the embedding orchestrator in sync
    /// with file/folder lifecycle events. All methods log + swallow exceptions; they never fail the caller.
    /// </summary>
    public interface IFileEmbeddingSync
    {
        ValueTask EnqueueOnCreate(Guid fileId, Guid userId, CancellationToken ct = default);
        ValueTask EnqueueOnRename(Guid fileId, Guid userId, CancellationToken ct = default);

        Task UpdateMetadataOnMove(Guid fileId, Guid newFolderId, string newFolderPath, Guid oldFolderId, Guid userId, CancellationToken ct = default);
        Task UpdateMetadataOnTrash(Guid fileId, bool isTrash, Guid parentFolderId, Guid userId, CancellationToken ct = default);

        Task DeleteOnHardDelete(Guid fileId, Guid parentFolderId, Guid userId, CancellationToken ct = default);

        Task CascadeFolderTrash(Guid folderId, bool isTrash, Guid userId, CancellationToken ct = default);
        Task CascadeFolderDelete(Guid folderId, Guid userId, CancellationToken ct = default);
    }
}
