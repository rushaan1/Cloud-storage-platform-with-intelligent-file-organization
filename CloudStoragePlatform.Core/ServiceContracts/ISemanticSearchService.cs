using CloudStoragePlatform.Core.DTO;

namespace CloudStoragePlatform.Core.ServiceContracts
{
    public interface ISemanticSearchService
    {
        /// <summary>
        /// Embeds the query, queries Pinecone for similar file vectors scoped to the current user
        /// (filter: userId, type=file, isTrash=false), and returns a BulkResponse.
        /// In hybrid mode (default), also unions in a name-substring search for files whose name contains the query.
        /// On Pinecone/Vertex failure, falls back to substring-only so the endpoint never 5xx's.
        /// </summary>
        Task<BulkResponse> SearchAsync(string query, int topK = 20, bool hybrid = true, CancellationToken ct = default);
    }
}
