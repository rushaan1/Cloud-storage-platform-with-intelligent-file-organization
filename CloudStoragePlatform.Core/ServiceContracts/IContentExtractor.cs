using File = CloudStoragePlatform.Core.Domain.Entities.File;

namespace CloudStoragePlatform.Core.ServiceContracts
{
    public interface IContentExtractor
    {
        /// <summary>
        /// Produces the text string to feed into the embedding model for a given file.
        /// Always returns at least the structured header (filename, path, folder, type).
        /// Content extraction is best-effort and may swallow errors.
        /// </summary>
        Task<string> ExtractAsync(File file, CancellationToken ct = default);
    }
}
