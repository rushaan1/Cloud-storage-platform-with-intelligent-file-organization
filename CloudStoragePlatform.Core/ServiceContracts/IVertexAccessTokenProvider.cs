namespace CloudStoragePlatform.Core.ServiceContracts
{
    public interface IVertexAccessTokenProvider
    {
        Task<string> GetTokenAsync(CancellationToken ct = default);
    }
}
