import { NgModule } from '@angular/core';
import { BrowserModule } from '@angular/platform-browser';

import { AppRoutingModule } from './app-routing.module';
import { AppComponent } from './app.component';
import { WorkspaceModule } from './workspace/workspace.module';
import { NotificationCenterComponent } from './notification-center/notification-center.component';
import {AccountModule} from "./account/account.module";
import {HTTP_INTERCEPTORS} from "@angular/common/http";
import {RequestInterceptor} from "./services/ApiServices/request-interceptor.service";
import {FolderSuggestionOverlayComponent} from "./workspace/folder-suggestion-overlay/folder-suggestion-overlay.component";
import {TagFilterOverlayComponent} from "./workspace/tag-filter-overlay/tag-filter-overlay.component";

@NgModule({
  declarations: [
    AppComponent
  ],
  imports: [
    BrowserModule,
    AppRoutingModule,
    WorkspaceModule,
    AccountModule,
    FolderSuggestionOverlayComponent,
    TagFilterOverlayComponent
  ],
  providers: [
    {provide: HTTP_INTERCEPTORS, useClass: RequestInterceptor, multi: true}
  ],
  bootstrap: [AppComponent]
})
export class AppModule { }
