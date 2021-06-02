import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { map, mergeMap } from 'rxjs/operators';
import { RegistrationsService } from 'src/app/core/api/services';
import { EvacueeProfile } from '../api/models';
import { CacheService } from './cache.service';

@Injectable({
  providedIn: 'root'
})
export class EvacueeProfileService {
  private cachedProfileId: string;

  constructor(
    private registrationsService: RegistrationsService,
    private cacheService: CacheService
  ) {}

  /**
   * Insert new profile
   *
   * @returns string[] list of security questions
   */
  public upsertProfile(evacProfile: EvacueeProfile): Observable<string> {
    return this.registrationsService
      .registrationsUpsertRegistrantProfile({ body: evacProfile })
      .pipe(
        map((profileId) => {
          return profileId;
        })
      );
  }

  /**
   * Get Profile ID currently stored in cache.
   */
  public getCurrentProfileId() {
    return this.cachedProfileId
      ? this.cachedProfileId
      : JSON.parse(this.cacheService.get('profileId'));
  }

  /**
   * Store a Profile ID in the cache.
   *
   * @param id ID to store in cache
   */
  public setCurrentProfileId(id: string) {
    this.cacheService.set('profileId', id);
    this.cachedProfileId = id;
  }

  /**
   * Remove "profileId" from cache
   */
  public clearCurrentProfileId(): void {
    this.cacheService.remove('profileId');
  }
}
