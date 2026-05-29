namespace CloudStoragePlatform.Core.ServiceContracts
{
    public record PineconeUpsertItem(string Id, float[] Vector, IDictionary<string, object> Metadata);
    public record PineconeMatch(string Id, float Score, IDictionary<string, object>? Metadata, float[]? Values);
    public record PineconeVector(string Id, float[] Values, IDictionary<string, object>? Metadata);

    public interface IPineconeClient
    {
        Task UpsertAsync(string id, float[] vector, IDictionary<string, object> metadata, CancellationToken ct = default);
        Task UpsertBatchAsync(IEnumerable<PineconeUpsertItem> items, CancellationToken ct = default);
        Task<List<PineconeMatch>> QueryAsync(
            float[] vector,
            int topK,
            IDictionary<string, object>? filter = null,
            bool includeMetadata = true,
            bool includeValues = false,
            CancellationToken ct = default);
        Task UpdateMetadataAsync(string id, IDictionary<string, object> metadata, CancellationToken ct = default);
        Task DeleteAsync(string id, CancellationToken ct = default);
        Task DeleteManyAsync(IEnumerable<string> ids, CancellationToken ct = default);
        Task DeleteByFilterAsync(IDictionary<string, object> filter, CancellationToken ct = default);
        Task<List<PineconeVector>> FetchAsync(IEnumerable<string> ids, CancellationToken ct = default);
    }
}
