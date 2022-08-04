import { Injectable } from '@angular/core';
import { FormBuilder, FormGroup } from '@angular/forms';
import { Router } from '@angular/router';
import { SelectedPathType } from '../models/appBase.model';
import { DashboardBanner } from '../models/dialog-content.model';
import { EvacuationFileModel } from '../models/evacuation-file.model';
import { EvacueeSearchContextModel } from '../models/evacuee-search-context.model';
import { RegistrantProfileModel } from '../models/registrant-profile.model';
import { DigitalOptionService } from '../services/compute/digitalOption.service';
import { PaperOptionService } from '../services/compute/paperOption.service';
import { RemoteExtOptionService } from '../services/compute/remoteExtOption.service';
import { AppBaseService } from '../services/helper/appBase.service';
import { DataService } from '../services/helper/data.service';

export interface SearchOptionsService {
  idSearchQuestion: string;
  optionType: SelectedPathType;
  loadDefaultComponent(): void;
  createForm(formType: string): FormGroup;
  search(
    value: string | EvacueeSearchContextModel,
    type?: string
  ): Promise<boolean> | void;
  getDashboardBanner(fileStatus: string): DashboardBanner;
  loadEssFile(): Promise<EvacuationFileModel>;
  loadEvcaueeProfile(registrantId: string): Promise<RegistrantProfileModel>;
}

@Injectable({
  providedIn: 'root'
})
export class OptionInjectionService {
  constructor(
    protected appBaseService: AppBaseService,
    protected router: Router,
    protected dataService: DataService,
    protected builder: FormBuilder
  ) {}

  public get instance(): SearchOptionsService {
    return this.selectService();
  }

  private selectService() {
    if (
      this.appBaseService?.appModel?.selectedUserPathway ===
      SelectedPathType.digital
    ) {
      return new DigitalOptionService(
        this.router,
        this.dataService,
        this.builder
      );
    } else if (
      this.appBaseService?.appModel?.selectedUserPathway ===
      SelectedPathType.paperBased
    ) {
      return new PaperOptionService(
        this.router,
        this.dataService,
        this.builder
      );
    } else if (
      this.appBaseService?.appModel?.selectedUserPathway ===
      SelectedPathType.remoteExtensions
    ) {
      return new RemoteExtOptionService(
        this.router,
        this.dataService,
        this.builder
      );
    }
  }
}
