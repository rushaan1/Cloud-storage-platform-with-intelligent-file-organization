import { ChangeDetectorRef, Component, OnDestroy, OnInit } from "@angular/core";
import { CommonModule } from "@angular/common";
import { Subscription } from "rxjs";
import {
  SmartSuggestionService,
  SuggestionEntry,
  FolderOption
} from "../../services/StateManagementServices/smart-suggestion.service";
import { FilesAndFoldersService } from "../../services/ApiServices/files-and-folders.service";
import { EventService } from "../../services/event-service.service";
import { Utils } from "../../Utils";

@Component({
  selector: "folder-suggestion-overlay",
  standalone: true,
  imports: [CommonModule],
  templateUrl: "./folder-suggestion-overlay.component.html",
  styleUrl: "./folder-suggestion-overlay.component.css"
})
export class FolderSuggestionOverlayComponent implements OnInit, OnDestroy {
  entries: SuggestionEntry[] = [];
  collapsed = false;
  private sub?: Subscription;

  constructor(
    private smartSuggestions: SmartSuggestionService,
    private filesService: FilesAndFoldersService,
    private eventService: EventService,
    private cd: ChangeDetectorRef
  ) {}

  ngOnInit(): void {
    this.sub = this.smartSuggestions.entries$.subscribe(entries => {
      this.entries = entries;
      this.cd.detectChanges();
    });
  }

  ngOnDestroy(): void {
    this.sub?.unsubscribe();
  }

  selectedOption(entry: SuggestionEntry): FolderOption {
    return entry.options.find(o => o.folderId === entry.selectedFolderId) || entry.options[0];
  }

  onSelectFolder(entry: SuggestionEntry, folderId: string) {
    this.smartSuggestions.setSelectedFolder(entry.fileId, folderId);
  }

  displayPath(path: string): string {
    const idx = path.toLowerCase().indexOf("\\home");
    const p = idx >= 0 ? path.substring(idx) : path;
    return p.replace(/\\/g, "/");
  }

  accept(entry: SuggestionEntry) {
    const option = this.selectedOption(entry);
    if (!option || entry.accepting) {
      return;
    }
    this.smartSuggestions.markAccepting(entry.fileId, true);
    const apiPath = Utils.constructFilePathForApi(Utils.cleanPath(option.folderPath));
    this.filesService.batchMoveFiles([entry.fileId], apiPath).subscribe({
      next: () => {
        this.smartSuggestions.removeEntry(entry.fileId);
        this.eventService.emit("addNotif", ["Moved '" + Utils.resize(entry.fileName, 25) + "' to " + option.folderName, 8000]);
      },
      error: () => {
        this.smartSuggestions.markAccepting(entry.fileId, false);
        this.eventService.emit("addNotif", ["Couldn't move '" + Utils.resize(entry.fileName, 25) + "'", 8000]);
      }
    });
  }

  acceptAll() {
    // Snapshot to avoid mutating the array while iterating (accept() removes entries).
    [...this.entries].forEach(entry => this.accept(entry));
  }

  dismiss(entry: SuggestionEntry) {
    this.smartSuggestions.removeEntry(entry.fileId);
  }

  dismissAll() {
    this.smartSuggestions.clearAll();
  }

  toggleCollapse() {
    this.collapsed = !this.collapsed;
  }

  trackByFileId(_index: number, entry: SuggestionEntry) {
    return entry.fileId;
  }
}
