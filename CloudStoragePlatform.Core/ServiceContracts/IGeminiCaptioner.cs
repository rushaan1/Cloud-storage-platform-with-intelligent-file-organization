namespace CloudStoragePlatform.Core.ServiceContracts
{
    public interface IGeminiCaptioner
    {
        Task<string> CaptionAsync(byte[] imageBytes, string mimeType, CancellationToken ct = default);
    }
}
