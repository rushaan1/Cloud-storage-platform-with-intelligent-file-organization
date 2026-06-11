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
        private readonly IFolderEmbeddingRepository _folderEmbRepo;
        private readonly IContentExtractor _extractor;
        private readonly UserIdentification _ui;
        private readonly IConfiguration _config;
        private readonly ILogger<FolderSuggestionService> _logger;

        public FolderSuggestionService(
            IVertexEmbeddingClient vertex,
            IPineconeClient pinecone,
            IFilesRepository filesRepo,
            IFoldersRepository foldersRepo,
            IFolderEmbeddingRepository folderEmbRepo,
            IContentExtractor extractor,
            UserIdentification ui,
            IConfiguration config,
            ILogger<FolderSuggestionService> logger)
        {
            _vertex = vertex;
            _pinecone = pinecone;
            _filesRepo = filesRepo;
            _foldersRepo = foldersRepo;
            _folderEmbRepo = folderEmbRepo;
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
            int minFolderFiles = int.TryParse(_config["Ai:Suggestion:MinFolderFiles"], out var mf) ? mf : 5;

            string homeFolderPath = Path.Combine(_config["InitialPathForStorage"] ?? string.Empty, "home");

            // Identify the upload destination (current parent) and whether it is the root home folder.
            var parent = await _foldersRepo.GetFolderByFolderId(currentParentFolderId);
            bool parentIsHome = parent != null && string.Equals(parent.FolderPath, homeFolderPath, StringComparison.OrdinalIgnoreCase);

            // When uploading into a specific (non-home) folder, only suggest a different home for the file
            // if that destination folder is well-established: >= N live files AND its centroid was computed from >= N files.
            if (!parentIsHome)
            {
                if (parent == null) return new List<FolderSuggestion>();
                int liveFileCount = parent.Files.Count(f => !f.IsTrash);
                var parentEmb = await _folderEmbRepo.GetByFolderId(currentParentFolderId);
                int centroidFileCount = parentEmb?.FileCount ?? 0;
                if (liveFileCount < minFolderFiles || centroidFileCount < minFolderFiles)
                    return new List<FolderSuggestion>();
            }

            var filter = new Dictionary<string, object>
            {
                ["userId"] = userId.ToString(),
                ["type"] = "folder"
            };

            List<PineconeMatch> matches;
            try
            {
                // Fetch extras so we can drop the current parent + home + trashed folders and still fill topK.
                matches = await _pinecone.QueryAsync(vector, topK + 4, filter, includeMetadata: true, includeValues: false, ct: ct);
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

            var results = new List<FolderSuggestion>();
            foreach (var m in matches)
            {
                if (m.Score < minScore) continue;
                if (!TryGetFolderId(m, out var folderId)) continue;
                if (folderId == currentParentFolderId) continue;            // never suggest the folder it's already in
                var folder = await _foldersRepo.GetFolderByFolderId(folderId);
                if (folder == null || folder.IsTrash) continue;
                if (string.Equals(folder.FolderPath, homeFolderPath, StringComparison.OrdinalIgnoreCase)) continue; // never suggest the root home folder
                results.Add(new FolderSuggestion(folder.FolderId, folder.FolderPath, folder.FolderName, m.Score));
                if (results.Count >= topK) break;
            }

            if (results.Count == 0) return new List<FolderSuggestion>();

            // The top candidate must beat the current folder's own centroid by a margin (only nudge when there's a clearly better fit).
            // WAIVED when uploading into the root home folder, since home is the catch-all and has no meaningful "fit".
            if (!parentIsHome && currentParentScore.HasValue && results[0].Score < currentParentScore.Value + margin)
                return new List<FolderSuggestion>();

            return results;
        }

        public async Task<List<FileSuggestionEntry>> SuggestForFolderContentsAsync(Guid folderId, int topK = 3, CancellationToken ct = default)
        {
            if (_ui.User == null) throw new InvalidOperationException("User context not set");

            var folder = await _foldersRepo.GetFolderByFolderId(folderId);
            if (folder == null) return new List<FileSuggestionEntry>();

            var files = folder.Files.Where(f => !f.IsTrash).ToList();
            if (files.Count == 0) return new List<FileSuggestionEntry>();

            // Batch-fetch all file vectors from Pinecone in one round-trip.
            var fileIdStrings = files.Select(f => f.FileId.ToString()).ToList();
            List<PineconeVector> fetched;
            try
            {
                fetched = await _pinecone.FetchAsync(fileIdStrings, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Pinecone batch fetch failed for AI organise FolderId={FolderId}", folderId);
                return new List<FileSuggestionEntry>();
            }

            var vecByFileId = fetched
                .Where(v => v.Values != null && v.Values.Length > 0)
                .ToDictionary(v => v.Id, v => v.Values);

            var results = new List<FileSuggestionEntry>();
            foreach (var f in files)
            {
                if (!vecByFileId.TryGetValue(f.FileId.ToString(), out var vec)) continue; // not yet embedded
                var suggestions = await SuggestFoldersForVectorAsync(vec, folderId, _ui.User.Id, topK, ct);
                if (suggestions.Count > 0)
                    results.Add(new FileSuggestionEntry(f.FileId, f.FileName, suggestions));
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
