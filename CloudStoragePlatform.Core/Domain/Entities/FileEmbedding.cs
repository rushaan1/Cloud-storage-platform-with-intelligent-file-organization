using CloudStoragePlatform.Core.Enums;
using System.ComponentModel.DataAnnotations;

namespace CloudStoragePlatform.Core.Domain.Entities
{
    public class FileEmbedding
    {
        [Key]
        public Guid Id { get; set; }

        public Guid FileId { get; set; }
        public virtual File File { get; set; } = null!;

        public Guid UserId { get; set; }

        public EmbeddingStatus Status { get; set; } = EmbeddingStatus.Pending;

        public string? VectorId { get; set; }

        public string Model { get; set; } = "text-embedding-005";

        public int Dimension { get; set; } = 768;

        public string? ContentHash { get; set; }

        public DateTime? EmbeddedAt { get; set; }

        public string? ErrorMessage { get; set; }

        public int AttemptCount { get; set; } = 0;
    }
}
