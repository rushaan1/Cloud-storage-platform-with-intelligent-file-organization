namespace CloudStoragePlatform.Core.ServiceContracts
{
    public interface IGeminiTagger
    {
        /// <summary>
        /// Asks Gemini for 2-5 short topical tags (each 1-4 words) describing the given file content.
        /// Returns an empty list on failure (best-effort; never throws to the caller).
        /// </summary>
        Task<List<string>> TagAsync(string extractedText, CancellationToken ct = default);
    }
}
