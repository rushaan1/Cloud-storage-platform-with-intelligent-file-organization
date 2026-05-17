namespace CloudStoragePlatform.Core.ServiceContracts
{
    public record FolderSuggestion(Guid FolderId, string FolderPath, string FolderName, float Score);

    public interface IFolderSuggestionService
    {
        /// <summary>
        /// Suggests up to <paramref name="topK"/> folders most semantically similar to the given file.
        /// Excludes the file's current parent. Returns empty if the top candidate doesn't beat the
        /// current parent's centroid score by the configured margin.
        /// </summary>
        Task<List<FolderSuggestion>> SuggestFoldersForFileAsync(Guid fileId, int topK = 3, CancellationToken ct = default);

        /// <summary>
        /// Same as <see cref="SuggestFoldersForFileAsync"/> but uses a pre-computed vector to avoid a Pinecone fetch.
        /// Used by the orchestrator immediately after embedding a freshly-uploaded file.
        /// </summary>
        Task<List<FolderSuggestion>> SuggestFoldersForVectorAsync(float[] vector, Guid currentParentFolderId, Guid userId, int topK = 3, CancellationToken ct = default);
    }
}
