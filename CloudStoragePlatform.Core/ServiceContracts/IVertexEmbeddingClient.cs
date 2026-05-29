using CloudStoragePlatform.Core.Enums;

namespace CloudStoragePlatform.Core.ServiceContracts
{
    public interface IVertexEmbeddingClient
    {
        Task<float[]> EmbedAsync(string text, EmbeddingTaskType taskType, CancellationToken ct = default);
    }
}
