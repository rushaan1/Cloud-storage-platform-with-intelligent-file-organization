using CloudStoragePlatform.Core.Domain.Entities;
using CloudStoragePlatform.Core.Domain.RepositoryContracts;
using CloudStoragePlatform.Core.DTO;
using CloudStoragePlatform.Core.Enums;
using CloudStoragePlatform.Core.ServiceContracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using File = CloudStoragePlatform.Core.Domain.Entities.File;

namespace CloudStoragePlatform.Core.Services.Ai
{
    public class SemanticSearchService : ISemanticSearchService
    {
        private readonly IVertexEmbeddingClient _vertex;
        private readonly IPineconeClient _pinecone;
        private readonly IFilesRepository _filesRepo;
        private readonly IFoldersRepository _foldersRepo;
        private readonly UserIdentification _ui;
        private readonly IConfiguration _config;
        private readonly ThumbnailService _thumbnailService;
        private readonly ILogger<SemanticSearchService> _logger;

        public SemanticSearchService(
            IVertexEmbeddingClient vertex,
            IPineconeClient pinecone,
            IFilesRepository filesRepo,
            IFoldersRepository foldersRepo,
            UserIdentification ui,
            IConfiguration config,
            ThumbnailService thumbnailService,
            ILogger<SemanticSearchService> logger)
        {
            _vertex = vertex;
            _pinecone = pinecone;
            _filesRepo = filesRepo;
            _foldersRepo = foldersRepo;
            _ui = ui;
            _config = config;
            _thumbnailService = thumbnailService;
            _logger = logger;
        }

        public async Task<BulkResponse> SearchAsync(string query, int topK = 20, bool hybrid = true, CancellationToken ct = default)
        {
            var empty = new BulkResponse { folders = new List<FolderResponse>(), files = new List<FileResponse>() };
            if (string.IsNullOrWhiteSpace(query)) return empty;
            if (_ui.User == null) throw new InvalidOperationException("User context not set");

            int maxTopK = int.TryParse(_config["Ai:Search:MaxTopK"], out var mtk) ? mtk : 50;
            if (topK > maxTopK) topK = maxTopK;
            if (topK <= 0) topK = int.TryParse(_config["Ai:Search:DefaultTopK"], out var dtk) ? dtk : 20;
            float minScore = float.TryParse(_config["Ai:Search:MinScore"], out var ms) ? ms : 0.55f;
            Guid userId = _ui.User.Id;
            string trimmed = query.Trim();
            string lower = trimmed.ToLower();

            var semanticFiles = new List<File>();
            try
            {
                float[] queryVec = await _vertex.EmbedAsync(trimmed, EmbeddingTaskType.RetrievalQuery, ct);
                var filter = new Dictionary<string, object>
                {
                    ["userId"] = userId.ToString(),
                    ["type"] = "file",
                    ["isTrash"] = false
                };
                var matches = await _pinecone.QueryAsync(queryVec, topK, filter, includeMetadata: true, includeValues: false, ct: ct);
                var qualified = matches.Where(m => m.Score >= minScore).ToList();
                if (qualified.Count > 0)
                {
                    var ids = qualified
                        .Select(m => Guid.TryParse(m.Id, out var g) ? g : Guid.Empty)
                        .Where(g => g != Guid.Empty)
                        .ToList();
                    var files = await _filesRepo.GetFilesByIds(ids);
                    var dict = files.ToDictionary(f => f.FileId);
                    semanticFiles = ids.Where(id => dict.ContainsKey(id)).Select(id => dict[id]).ToList();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Semantic search failed for query '{Query}'; falling back to substring", trimmed);
            }

            var substringFiles = new List<File>();
            if (hybrid || semanticFiles.Count == 0)
            {
                try
                {
                    substringFiles = await _filesRepo.GetFilteredFiles(f => !f.IsTrash && f.FileName.ToLower().Contains(lower));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Substring fallback failed for query '{Query}'", trimmed);
                }
            }

            var seen = new HashSet<Guid>(semanticFiles.Select(f => f.FileId));
            var merged = new List<File>(semanticFiles);
            foreach (var f in substringFiles)
            {
                if (seen.Add(f.FileId)) merged.Add(f);
            }

            var fileResponses = merged
                .Select(f => f.ToFileResponse(_thumbnailService.GetThumbnail(f.FileId)))
                .ToList();

            // Folders are matched by name-substring (no semantic folder search in v1).
            var folderResponses = new List<FolderResponse>();
            try
            {
                string homeFolderPath = Path.Combine(_config["InitialPathForStorage"] ?? string.Empty, "home");
                var folders = await _foldersRepo.GetFilteredFolders(f => !f.IsTrash && f.FolderName.ToLower().Contains(lower));
                folderResponses = folders
                    .Where(f => !string.Equals(f.FolderPath, homeFolderPath, StringComparison.OrdinalIgnoreCase))
                    .Select(f => f.ToFolderResponse())
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Folder substring search failed for query '{Query}'", trimmed);
            }

            return new BulkResponse { folders = folderResponses, files = fileResponses };
        }
    }
}
