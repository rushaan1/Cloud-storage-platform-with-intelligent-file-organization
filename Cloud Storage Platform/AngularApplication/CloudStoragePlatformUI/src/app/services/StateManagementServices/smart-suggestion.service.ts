import { Injectable } from "@angular/core";
import { BehaviorSubject } from "rxjs";

export interface FolderOption {
  folderId: string;
  folderPath: string;
  folderName: string;
  score: number;
}

export interface SuggestionEntry {
  fileId: string;
  fileName: string;
  options: FolderOption[];
  selectedFolderId: string;
  accepting: boolean;
}

/**
 * Holds the live list of smart-upload folder suggestions.
 * Each uploaded file that the backend thinks is mis-filed produces one entry,
 * appended as the corresponding `folder_suggestion` SSE event is received.
 * The overlay component subscribes to entries$ and renders the bottom-right panel.
 */
@Injectable({ providedIn: "root" })
export class SmartSuggestionService {
  private entries = new BehaviorSubject<SuggestionEntry[]>([]);
  public entries$ = this.entries.asObservable();

  addSuggestion(fileId: string, fileName: string, options: FolderOption[]) {
    if (!options || options.length === 0) {
      return;
    }
    // Replace any existing entry for the same file, then append (keeps newest last).
    const filtered = this.entries.value.filter(e => e.fileId !== fileId);
    const entry: SuggestionEntry = {
      fileId,
      fileName,
      options,
      selectedFolderId: options[0].folderId,
      accepting: false
    };
    this.entries.next([...filtered, entry]);
  }

  setSelectedFolder(fileId: string, folderId: string) {
    this.entries.next(this.entries.value.map(e =>
      e.fileId === fileId ? { ...e, selectedFolderId: folderId } : e
    ));
  }

  markAccepting(fileId: string, accepting: boolean) {
    this.entries.next(this.entries.value.map(e =>
      e.fileId === fileId ? { ...e, accepting } : e
    ));
  }

  removeEntry(fileId: string) {
    this.entries.next(this.entries.value.filter(e => e.fileId !== fileId));
  }

  clearAll() {
    this.entries.next([]);
  }

  getAll(): SuggestionEntry[] {
    return this.entries.value;
  }
}
