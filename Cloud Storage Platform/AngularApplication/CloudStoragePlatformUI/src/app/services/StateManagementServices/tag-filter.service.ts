import { Injectable } from "@angular/core";
import { BehaviorSubject } from "rxjs";

/**
 * Holds the user's currently-selected tag filter as a Set of tag strings.
 * The viewer combines this with filesInViewer$ to derive visibleFiles.
 * Filtering semantics: a file is shown if it has at least ONE selected tag (OR logic).
 */
@Injectable({ providedIn: "root" })
export class TagFilterService {
  private selected = new BehaviorSubject<Set<string>>(new Set<string>());
  public selectedTags$ = this.selected.asObservable();

  toggle(tag: string): void {
    const next = new Set(this.selected.value);
    if (next.has(tag)) next.delete(tag); else next.add(tag);
    this.selected.next(next);
  }

  isSelected(tag: string): boolean {
    return this.selected.value.has(tag);
  }

  clear(): void {
    if (this.selected.value.size === 0) return;
    this.selected.next(new Set<string>());
  }

  getSelected(): Set<string> {
    return this.selected.value;
  }
}
