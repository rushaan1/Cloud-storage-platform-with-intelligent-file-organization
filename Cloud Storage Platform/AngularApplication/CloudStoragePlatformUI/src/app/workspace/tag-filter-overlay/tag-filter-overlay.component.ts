import { ChangeDetectorRef, Component, OnDestroy, OnInit } from "@angular/core";
import { CommonModule } from "@angular/common";
import { Subscription } from "rxjs";
import { FilesStateService } from "../../services/StateManagementServices/files-state.service";
import { TagFilterService } from "../../services/StateManagementServices/tag-filter.service";
import { File } from "../../models/File";

interface TagCount {
  tag: string;
  count: number;
}

@Component({
  selector: "tag-filter-overlay",
  standalone: true,
  imports: [CommonModule],
  templateUrl: "./tag-filter-overlay.component.html",
  styleUrl: "./tag-filter-overlay.component.css"
})
export class TagFilterOverlayComponent implements OnInit, OnDestroy {
  tagsWithCounts: TagCount[] = [];
  selected = new Set<string>();
  visible = false;

  private filesSub?: Subscription;
  private selectedSub?: Subscription;

  constructor(
    protected filesState: FilesStateService,
    protected tagFilter: TagFilterService,
    private cd: ChangeDetectorRef
  ) {}

  ngOnInit(): void {
    this.filesSub = this.filesState.filesInViewer$.subscribe((files: File[]) => {
      this.recomputeTags(files);
      // Drop selected tags that no longer appear in the current view (e.g. after navigating to a different folder).
      const available = new Set(this.tagsWithCounts.map(t => t.tag));
      const sel = this.tagFilter.getSelected();
      if (sel.size > 0) {
        let stillRelevant = false;
        for (const t of sel) { if (available.has(t)) { stillRelevant = true; break; } }
        if (!stillRelevant) this.tagFilter.clear();
      }
      this.cd.detectChanges();
    });

    this.selectedSub = this.tagFilter.selectedTags$.subscribe(s => {
      this.selected = s;
      this.cd.detectChanges();
    });
  }

  ngOnDestroy(): void {
    this.filesSub?.unsubscribe();
    this.selectedSub?.unsubscribe();
  }

  private recomputeTags(files: File[]): void {
    const counts = new Map<string, number>();
    for (const f of files) {
      const tags = f.tags || [];
      for (const raw of tags) {
        if (!raw) continue;
        const t = raw.trim();
        if (!t) continue;
        counts.set(t, (counts.get(t) || 0) + 1);
      }
    }
    this.tagsWithCounts = [...counts.entries()]
      .map(([tag, count]) => ({ tag, count }))
      .sort((a, b) => b.count - a.count || a.tag.localeCompare(b.tag));
  }

  hasTags(): boolean {
    return this.tagsWithCounts.length > 0;
  }

  toggleVisible(): void {
    this.visible = !this.visible;
  }

  close(): void {
    this.visible = false;
  }

  isSelected(tag: string): boolean {
    return this.selected.has(tag);
  }

  onTagClick(tag: string): void {
    this.tagFilter.toggle(tag);
  }

  clearAll(): void {
    this.tagFilter.clear();
  }
}
