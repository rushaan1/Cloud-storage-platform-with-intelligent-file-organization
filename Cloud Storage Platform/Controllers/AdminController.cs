using Cloud_Storage_Platform.Filters;
using CloudStoragePlatform.Core.Domain.RepositoryContracts;
using CloudStoragePlatform.Core.Enums;
using CloudStoragePlatform.Core.ServiceContracts;
using CloudStoragePlatform.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace Cloud_Storage_Platform.Controllers
{
    [ServiceFilter(typeof(IdentifyUser))]
    [Route("api/[controller]")]
    [ApiController]
    public class AdminController : ControllerBase
    {
        private readonly IFileEmbeddingRepository _embRepo;
        private readonly IEmbeddingOrchestrator _orchestrator;
        private readonly UserIdentification _ui;

        public AdminController(IFileEmbeddingRepository embRepo, IEmbeddingOrchestrator orchestrator, UserIdentification ui)
        {
            _embRepo = embRepo;
            _orchestrator = orchestrator;
            _ui = ui;
        }

        [HttpPost("embeddings/backfill")]
        public async Task<IActionResult> Backfill()
        {
            if (_ui.User == null) return Unauthorized();
            var missing = await _embRepo.GetMissingForUser(_ui.User.Id);
            int enqueued = 0;
            foreach (var emb in missing)
            {
                await _orchestrator.EnqueueAsync(new EmbeddingJob(emb.FileId, _ui.User.Id, EmbeddingReason.Backfill));
                enqueued++;
            }
            return Ok(new { enqueued, queueDepth = _orchestrator.PendingCount });
        }

        [HttpGet("embeddings/diagnostics")]
        public async Task<IActionResult> Diagnostics()
        {
            if (_ui.User == null) return Unauthorized();
            var all = await _embRepo.GetByUser(_ui.User.Id);
            var byStatus = all.GroupBy(e => e.Status).ToDictionary(g => g.Key.ToString(), g => g.Count());
            return Ok(new
            {
                total = all.Count,
                byStatus,
                pendingQueueDepth = _orchestrator.PendingCount,
                failedFiles = all
                    .Where(e => e.Status == EmbeddingStatus.Failed)
                    .Select(e => new { e.FileId, e.AttemptCount, e.ErrorMessage })
                    .Take(20)
                    .ToList()
            });
        }
    }
}
