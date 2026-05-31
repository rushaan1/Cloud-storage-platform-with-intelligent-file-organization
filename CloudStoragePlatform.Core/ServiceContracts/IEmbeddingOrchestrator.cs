using CloudStoragePlatform.Core.Enums;

namespace CloudStoragePlatform.Core.ServiceContracts
{
    public sealed record EmbeddingJob(Guid FileId, Guid UserId, EmbeddingReason Reason, bool SuppressSuggestion = false);

    public interface IEmbeddingOrchestrator
    {
        ValueTask EnqueueAsync(EmbeddingJob job, CancellationToken ct = default);
        int PendingCount { get; }
    }
}
