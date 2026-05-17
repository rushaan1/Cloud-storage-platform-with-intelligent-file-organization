using System.ComponentModel.DataAnnotations;

namespace CloudStoragePlatform.Core.Domain.Entities
{
    public class FolderEmbedding
    {
        [Key]
        public Guid Id { get; set; }

        public Guid FolderId { get; set; }
        public virtual Folder Folder { get; set; } = null!;

        public Guid UserId { get; set; }

        public string VectorId { get; set; } = string.Empty;

        public int FileCount { get; set; } = 0;

        public DateTime? LastComputedAt { get; set; }

        public bool IsStale { get; set; } = true;
    }
}
