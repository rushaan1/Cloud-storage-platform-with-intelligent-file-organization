using CloudStoragePlatform.Core.Domain.RepositoryContracts;
using CloudStoragePlatform.Core.Enums;
using CloudStoragePlatform.Core.ServiceContracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using File = CloudStoragePlatform.Core.Domain.Entities.File;

namespace CloudStoragePlatform.Core.Services.Ai
{
    public class FolderSuggestionService : IFolderSuggestionService
    {
        private readonly IVertexEmbeddingClient _vertex;
        private readonly IPineconeClient _pinecone;
        private readonly IFilesRepository _filesRepo;
        private readonly IFoldersRepository _foldersRepo;
        private readonly IContentExtractor _extractor;
        private readonly UserIdentification _ui;
        private readonly IConfiguration _config;
        private readonly ILogger<FolderSuggestionService> _logger;

        public FolderSuggestionService(
            IVertexEmbeddingClient vertex,
            IPineconeClient pinecone,
            IFilesRepository filesRepo,
            IFoldersRepository foldersRepo,
            IContentExtractor extractor,
            UserIdentification ui,
            IConfiguration config,
            ILogger<FolderSuggestionService> logger)
        {
            _vertex = vertex;
            _pinecone = pinecone;
            _filesRepo = filesRepo;
            _foldersRepo = foldersRepo;
            _extractor = extractor;
            _ui = ui;
            _config = config;
            _logger = logger;
        }

        public async Task<List<FolderSuggestion>> SuggestFoldersForFileAsync(Guid fileId, int topK = 3, CancellationToken ct = default)
        {
            if (_ui.User == null) throw new InvalidOperationException("User context not set");
            File? file = await _filesRepo.GetFileByFileId(fileId);
            if (file == null) return new List<FolderSuggestion>();

            float[]? vector = null;
            try
            {
                var fetched = await _pinecone.FetchAsync(new[] { fileId.ToString() }, ct);
                if (fetched.Count > 0 && fetched[0].Values != null && fetched[0].Values.Length > 0)
                    vector = fetched[0].Values;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Pinecone fetch failed for FileId={FileId}", fileId);
            }

            if (vector == null)
            {
                try
                {
                    string text = await _extractor.ExtractAsync(file, ct);
                    vector = await _vertex.EmbedAsync(text, EmbeddingTaskType.RetrievalDocument, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "On-the-fly embed failed for FileId={FileId}; cannot suggest", fileId);
                    return new List<FolderSuggestion>();
                }
            }

            return await SuggestFoldersForVectorAsync(vector, file.ParentFolderId, _ui.User.Id, topK, ct);
        }

        public async Task<List<FolderSuggestion>> SuggestFoldersForVectorAsync(float[] vector, Guid currentParentFolderId, Guid userId, int topK = 3, CancellationToken ct = default)
        {
            float minScore = float.TryParse(_config["Ai:Suggestion:MinScore"], out var ms) ? ms : 0.5f;
            float margin = float.TryParse(_config["Ai:Suggestion:Margin"], out var mg) ? mg : 0.1f;

            var filter = new Dictionary<string, object>
            {
                ["userId"] = userId.ToString(),
                ["type"] = "folder"
            };

            List<PineconeMatch> matches;
            try
            {
                matches = await _pinecone.QueryAsync(vector, topK + 1, filter, includeMetadata: true, includeValues: false, ct: ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Pinecone query for folder centroids failed");
                return new List<FolderSuggestion>();
            }

            if (matches.Count == 0) return new List<FolderSuggestion>();

            float? currentParentScore = null;
            foreach (var m in matches)
            {
                if (TryGetFolderId(m, out var fid) && fid == currentParentFolderId)
                {
                    currentParentScore = m.Score;
                    break;
                }
            }

            var qualified = matches
                .Where(m => m.Score >= minScore)
                .Where(m => TryGetFolderId(m, out var fid) && fid != currentParentFolderId)
                .Take(topK)
                .ToList();

            if (qualified.Count == 0) return new List<FolderSuggestion>();

            if (currentParentScore.HasValue && qualified[0].Score < currentParentScore.Value + margin)
                return new List<FolderSuggestion>();

            var results = new List<FolderSuggestion>();
            foreach (var m in qualified)
            {
                if (!TryGetFolderId(m, out var folderId)) continue;
                var folder = await _foldersRepo.GetFolderByFolderId(folderId);
                if (folder == null) continue;
                results.Add(new FolderSuggestion(folderId, folder.FolderPath, folder.FolderName, m.Score));
            }
            return results;
        }

        private static bool TryGetFolderId(PineconeMatch m, out Guid id)
        {
            id = Guid.Empty;
            if (m.Metadata == null) return false;
            if (!m.Metadata.TryGetValue("folderId", out var val)) return false;
            return Guid.TryParse(val?.ToString(), out id);
        }
    }
}
