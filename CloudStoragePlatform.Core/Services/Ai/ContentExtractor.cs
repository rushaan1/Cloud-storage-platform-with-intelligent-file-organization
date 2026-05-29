using CloudStoragePlatform.Core.Enums;
using CloudStoragePlatform.Core.ServiceContracts;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text;
using UglyToad.PdfPig;
using File = CloudStoragePlatform.Core.Domain.Entities.File;
using SystemFile = System.IO.File;

namespace CloudStoragePlatform.Core.Services.Ai
{
    public class ContentExtractor : IContentExtractor
    {
        private readonly UserIdentification _ui;
        private readonly IGeminiCaptioner _captioner;
        private readonly IConfiguration _config;
        private readonly ILogger<ContentExtractor> _logger;

        public ContentExtractor(UserIdentification ui, IGeminiCaptioner captioner, IConfiguration config, ILogger<ContentExtractor> logger)
        {
            _ui = ui;
            _captioner = captioner;
            _config = config;
            _logger = logger;
        }

        public async Task<string> ExtractAsync(File file, CancellationToken ct = default)
        {
            int maxChars = int.TryParse(_config["Ai:Embedding:MaxCharsForEmbedding"], out var x) ? x : 6000;

            string physicalPath = Path.Combine(_ui.PhysicalStoragePath, file.FileId.ToString());
            string parentName = file.ParentFolder?.FolderName ?? "(root)";
            string folderPath = file.ParentFolder?.FolderPath ?? "";

            var sb = new StringBuilder();
            sb.Append("[FILE: ").Append(file.FileName).Append("]\n");
            sb.Append("[PATH: ").Append(folderPath).Append("]\n");
            sb.Append("[FOLDER: ").Append(parentName).Append("]\n");
            sb.Append("[TYPE: ").Append(file.FileType).Append("]\n");

            string content = string.Empty;
            try
            {
                if (SystemFile.Exists(physicalPath))
                {
                    content = file.FileType switch
                    {
                        FileType.Document => await ExtractDocumentAsync(physicalPath, file.FileName, ct),
                        FileType.Image or FileType.GIF => await CaptionImageAsync(physicalPath, file.FileType, ct),
                        _ => string.Empty
                    };
                }
                else
                {
                    _logger.LogWarning("Physical file missing for FileId={FileId} at {Path}", file.FileId, physicalPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Content extraction failed for FileId={FileId}; falling back to header-only", file.FileId);
            }

            if (!string.IsNullOrWhiteSpace(content))
            {
                sb.Append(Sanitize(content));
            }

            string full = sb.ToString();
            if (full.Length > maxChars) full = full.Substring(0, maxChars);
            return full;
        }

        private static async Task<string> ExtractDocumentAsync(string path, string fileName, CancellationToken ct)
        {
            string ext = fileName.Split('.').Last().ToLowerInvariant();
            switch (ext)
            {
                case "txt":
                    using (var reader = new StreamReader(path))
                        return await reader.ReadToEndAsync();
                case "pdf":
                    return await Task.Run(() =>
                    {
                        using var pdf = PdfDocument.Open(path);
                        var sb = new StringBuilder();
                        foreach (var page in pdf.GetPages())
                        {
                            sb.AppendLine(page.Text);
                            if (sb.Length > 60000) break;
                        }
                        return sb.ToString();
                    }, ct);
                case "docx":
                    return await Task.Run(() =>
                    {
                        using var docx = WordprocessingDocument.Open(path, false);
                        var body = docx.MainDocumentPart?.Document?.Body;
                        if (body == null) return string.Empty;
                        return string.Join("\n", body.Descendants<Text>().Select(t => t.Text));
                    }, ct);
                default:
                    return string.Empty;
            }
        }

        private async Task<string> CaptionImageAsync(string path, FileType fileType, CancellationToken ct)
        {
            byte[] bytes = await SystemFile.ReadAllBytesAsync(path, ct);
            string mime = fileType == FileType.GIF ? "image/gif" : "image/jpeg";
            return await _captioner.CaptionAsync(bytes, mime, ct);
        }

        private static string Sanitize(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c == '\n' || c == '\r' || c == '\t' || !char.IsControl(c))
                    sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
