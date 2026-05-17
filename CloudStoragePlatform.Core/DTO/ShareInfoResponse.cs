using System;

namespace CloudStoragePlatform.Core.DTO
{
    public class ShareInfoResponse
    {
        public Guid SharingId { get; set; }
        public DateTime? ShareLinkExpiry { get; set; }
        public DateTime? ShareLinkCreateDate { get; set; }
        public int? Visits { get; set; }
    }
}
