﻿// -------------------------------------------------------------------------
//  Copyright © 2021 Province of British Columbia
//
//  Licensed under the Apache License, Version 2.0 (the "License");
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
//
//  https://www.apache.org/licenses/LICENSE-2.0
//
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.
// -------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutoMapper;
using EMBC.ESS.Utilities.Dynamics;
using EMBC.ESS.Utilities.Dynamics.Microsoft.Dynamics.CRM;
using EMBC.ESS.Utilities.Extensions;
using Microsoft.OData.Client;

namespace EMBC.ESS.Resources.Cases.Evacuations
{
    public class EvacuationRepository : IEvacuationRepository
    {
        private readonly EssContext essContext;
        private readonly IMapper mapper;

        public EvacuationRepository(EssContext essContext, IMapper mapper)
        {
            this.essContext = essContext;
            this.mapper = mapper;
        }

        public async Task<string> Create(EvacuationFile evacuationFile)
        {
            VerifyEvacuationFileInvariants(evacuationFile);

            var primaryContact = essContext.contacts.Where(c => c.statecode == (int)EntityState.Active && c.contactid == Guid.Parse(evacuationFile.PrimaryRegistrantId)).SingleOrDefault();
            if (primaryContact == null) throw new Exception($"Primary registrant {evacuationFile.PrimaryRegistrantId} not found");

            var file = mapper.Map<era_evacuationfile>(evacuationFile);
            file.era_evacuationfileid = Guid.NewGuid();

            essContext.AddToera_evacuationfiles(file);
            essContext.SetLink(file, nameof(era_evacuationfile.era_EvacuatedFromID), essContext.LookupJurisdictionByCode(file._era_evacuatedfromid_value?.ToString()));
            AssignPrimaryRegistrant(file, primaryContact);
            AssignToTask(file, evacuationFile.TaskId);
            AddPets(file);

            AddNeedsAssessment(file, file.era_CurrentNeedsAssessmentid);

            await essContext.SaveChangesAsync();

            essContext.Detach(file);

            //get the autogenerated evacuation file number
            var essFileNumber = essContext.era_evacuationfiles.Where(f => f.era_evacuationfileid == file.era_evacuationfileid).Select(f => f.era_name).Single();

            essContext.DetachAll();

            return essFileNumber;
        }

        public async Task<string> Update(EvacuationFile evacuationFile)
        {
            VerifyEvacuationFileInvariants(evacuationFile);

            var currentFile = essContext.era_evacuationfiles
                .Where(f => f.era_name == evacuationFile.Id).SingleOrDefault();
            if (currentFile == null) throw new Exception($"Evacuation file {evacuationFile.Id} not found");

            var primaryContact = essContext.contacts.Where(c => c.statecode == (int)EntityState.Active && c.contactid == Guid.Parse(evacuationFile.PrimaryRegistrantId)).SingleOrDefault();
            if (primaryContact == null) throw new Exception($"Primary registrant {evacuationFile.PrimaryRegistrantId} not found");

            RemovePets(currentFile);
            essContext.Detach(currentFile);
            var file = mapper.Map<era_evacuationfile>(evacuationFile);
            file.era_evacuationfileid = currentFile.era_evacuationfileid;

            essContext.AttachTo(nameof(essContext.era_evacuationfiles), file);
            essContext.SetLink(file, nameof(era_evacuationfile.era_EvacuatedFromID), essContext.LookupJurisdictionByCode(file._era_evacuatedfromid_value?.ToString()));

            foreach (var member in file.era_era_evacuationfile_era_householdmember_EvacuationFileid)
            {
                if (member.era_householdmemberid.HasValue)
                {
                    //update member
                    essContext.AttachTo(nameof(essContext.era_householdmembers), member);
                    essContext.UpdateObject(member);
                    AssignHouseholdMember(file, member);
                }
            }

            essContext.UpdateObject(file);
            AssignPrimaryRegistrant(file, primaryContact);
            AssignToTask(file, evacuationFile.TaskId);
            AddPets(file);

            await essContext.SaveChangesAsync();
            essContext.DetachAll();
            essContext.AttachTo(nameof(essContext.era_evacuationfiles), file);
            AddNeedsAssessment(file, file.era_CurrentNeedsAssessmentid);

            await essContext.SaveChangesAsync();

            essContext.DetachAll();

            return file.era_name;
        }

        private void AddNeedsAssessment(era_evacuationfile file, era_needassessment needsAssessment)
        {
            essContext.AddToera_needassessments(needsAssessment);
            essContext.SetLink(file, nameof(era_evacuationfile.era_CurrentNeedsAssessmentid), needsAssessment);
            essContext.AddLink(file, nameof(era_evacuationfile.era_needsassessment_EvacuationFile), needsAssessment);
            essContext.SetLink(needsAssessment, nameof(era_needassessment.era_EvacuationFile), file);
            essContext.SetLink(needsAssessment, nameof(era_needassessment.era_Jurisdictionid), essContext.LookupJurisdictionByCode(needsAssessment._era_jurisdictionid_value?.ToString()));
            if (needsAssessment._era_reviewedbyid_value.HasValue)
            {
                var teamMember = essContext.era_essteamusers.ByKey(needsAssessment._era_reviewedbyid_value).GetValue();
                essContext.SetLink(needsAssessment, nameof(era_needassessment.era_ReviewedByid), teamMember);
                essContext.AddLink(teamMember, nameof(era_essteamuser.era_era_essteamuser_era_needassessment_ReviewedByid), needsAssessment);
            }

            foreach (var member in needsAssessment.era_era_householdmember_era_needassessment)
            {
                if (member.era_householdmemberid.HasValue)
                {
                    //update member
                    essContext.AttachTo(nameof(essContext.era_householdmembers), member);
                    essContext.UpdateObject(member);
                }
                else
                {
                    //create member
                    member.era_householdmemberid = Guid.NewGuid();
                    essContext.AddToera_householdmembers(member);
                }
                AssignHouseholdMember(file, member);
                AssignHouseholdMember(needsAssessment, member);
            }
        }

        private void AssignPrimaryRegistrant(era_evacuationfile file, contact primaryContact)
        {
            essContext.AddLink(primaryContact, nameof(primaryContact.era_evacuationfile_Registrant), file);
            essContext.SetLink(file, nameof(era_evacuationfile.era_Registrant), primaryContact);
        }

        private void AssignToTask(era_evacuationfile file, string taskId)
        {
            if (string.IsNullOrEmpty(taskId)) return;
            var task = essContext.era_tasks.Where(t => t.era_name == taskId).SingleOrDefault();
            if (task == null) throw new Exception($"Task {taskId} not found");
            essContext.AddLink(task, nameof(era_task.era_era_task_era_evacuationfileId), file);
        }

        private void AssignHouseholdMember(era_evacuationfile file, era_householdmember member)
        {
            if (member._era_registrant_value != null)
            {
                var registrant = essContext.contacts.Where(c => c.contactid == member._era_registrant_value).SingleOrDefault();
                if (registrant == null) throw new Exception($"Household member has registrant id {member._era_registrant_value} which was not found");
                essContext.AddLink(registrant, nameof(contact.era_contact_era_householdmember_Registrantid), member);
            }
            essContext.AddLink(file, nameof(era_evacuationfile.era_era_evacuationfile_era_householdmember_EvacuationFileid), member);
            essContext.SetLink(member, nameof(era_householdmember.era_EvacuationFileid), file);
        }

        private void AssignHouseholdMember(era_needassessment needsAssessment, era_householdmember member)
        {
            essContext.AddLink(member, nameof(era_householdmember.era_era_householdmember_era_needassessment), needsAssessment);
            //essContext.AddLink(needsAssessment, nameof(era_needassessment.era_era_householdmember_era_needassessment), member);
        }

        private void AddPets(era_evacuationfile file)
        {
            foreach (var pet in file.era_era_evacuationfile_era_animal_ESSFileid)
            {
                essContext.AddToera_animals(pet);
                essContext.AddLink(file, nameof(era_evacuationfile.era_era_evacuationfile_era_animal_ESSFileid), pet);
                essContext.SetLink(pet, nameof(era_animal.era_ESSFileid), file);
            }
        }

        private void RemovePets(era_evacuationfile file)
        {
            essContext.LoadProperty(file, nameof(era_evacuationfile.era_era_evacuationfile_era_animal_ESSFileid));
            foreach (var pet in file.era_era_evacuationfile_era_animal_ESSFileid)
            {
                essContext.DeleteObject(pet);
                essContext.DeleteLink(file, nameof(era_evacuationfile.era_era_evacuationfile_era_animal_ESSFileid), pet);
            }
        }

        private void VerifyEvacuationFileInvariants(EvacuationFile evacuationFile)
        {
            //Check invariants
            if (string.IsNullOrEmpty(evacuationFile.PrimaryRegistrantId))
            {
                throw new Exception($"The file has no associated primary registrant");
            }
            if (evacuationFile.NeedsAssessment == null)
            {
                throw new Exception($"File {evacuationFile.Id} must have a needs assessment");
            }

            if (evacuationFile.Id == null)
            {
                if (evacuationFile.NeedsAssessment.HouseholdMembers.Count(m => m.IsPrimaryRegistrant) != 1)
                {
                    throw new Exception($"File {evacuationFile.Id} must have a single primary registrant household member");
                }
            }
            else
            {
                if (evacuationFile.NeedsAssessment.HouseholdMembers.Count(m => m.IsPrimaryRegistrant) > 1)
                {
                    throw new Exception($"File {evacuationFile.Id} can not have multiple primary registrant household members");
                }
            }
        }

        public async Task<string> Delete(string fileId)
        {
            var evacuationFile = essContext.era_evacuationfiles
                .Where(ef => ef.era_name == fileId)
                .ToArray()
                .SingleOrDefault();

            if (evacuationFile != null)
            {
                essContext.DeactivateObject(evacuationFile, (int)EvacuationFileStatus.Inactive);
                await essContext.SaveChangesAsync();
            }
            essContext.DetachAll();

            return fileId;
        }

        private EvacuationFile MapEvacuationFile(era_evacuationfile file, bool maskSecurityPhrase = true) =>
            mapper.Map<EvacuationFile>(file, opt => opt.Items["MaskSecurityPhrase"] = maskSecurityPhrase.ToString());

        private static async Task ParallelLoadEvacuationFileAsync(EssContext ctx, era_evacuationfile file)
        {
            ctx.AttachTo(nameof(EssContext.era_evacuationfiles), file);

            var loadTasks = new List<Task>();
            loadTasks.Add(Task.Run(async () => await ctx.LoadPropertyAsync(file, nameof(era_evacuationfile.era_era_evacuationfile_era_animal_ESSFileid))));
            loadTasks.Add(Task.Run(async () => await ctx.LoadPropertyAsync(file, nameof(era_evacuationfile.era_era_evacuationfile_era_essfilenote_ESSFileID))));
            loadTasks.Add(Task.Run(async () => await ctx.LoadPropertyAsync(file, nameof(era_evacuationfile.era_TaskId))));

            loadTasks.Add(Task.Run(async () =>
            {
                await ctx.LoadPropertyAsync(file, nameof(era_evacuationfile.era_era_evacuationfile_era_evacueesupport_ESSFileId));
                foreach (var support in file.era_era_evacuationfile_era_evacueesupport_ESSFileId)
                {
                    ctx.AttachTo(nameof(EssContext.era_evacueesupports), support);
                    await ctx.LoadPropertyAsync(support, nameof(era_evacueesupport.era_era_householdmember_era_evacueesupport));
                }
            }));

            loadTasks.Add(Task.Run(async () =>
            {
                await ctx.LoadPropertyAsync(file, nameof(era_evacuationfile.era_era_evacuationfile_era_householdmember_EvacuationFileid));
                if (file.era_CurrentNeedsAssessmentid == null)
                    await ctx.LoadPropertyAsync(file, nameof(era_evacuationfile.era_CurrentNeedsAssessmentid));

                ctx.AttachTo(nameof(EssContext.era_needassessments), file.era_CurrentNeedsAssessmentid);
                await ctx.LoadPropertyAsync(file.era_CurrentNeedsAssessmentid, nameof(era_needassessment.era_era_householdmember_era_needassessment));

                foreach (var member in file.era_era_evacuationfile_era_householdmember_EvacuationFileid)
                {
                    if (member._era_registrant_value.HasValue)
                    {
                        ctx.AttachTo(nameof(EssContext.era_householdmembers), member);
                        await ctx.LoadPropertyAsync(member, nameof(era_householdmember.era_Registrant));
                        ctx.Detach(member);
                    }
                }

                foreach (var member in file.era_CurrentNeedsAssessmentid.era_era_householdmember_era_needassessment)
                {
                    if (member._era_registrant_value.HasValue)
                    {
                        ctx.AttachTo(nameof(EssContext.era_householdmembers), member);
                        await ctx.LoadPropertyAsync(member, nameof(era_householdmember.era_Registrant));
                        ctx.Detach(member);
                    }
                }
            }));
            await Task.WhenAll(loadTasks.ToArray());
        }

        private static async Task<IEnumerable<era_evacuationfile>> ParallelLoadEvacuationFilesAsync(EssContext ctx, IEnumerable<era_evacuationfile> files)
        {
            var readCtx = ctx.Clone();
            readCtx.MergeOption = MergeOption.NoTracking;

            //load files' properties
            await files.Select(file => ParallelLoadEvacuationFileAsync(readCtx, file)).ToArray().ForEachAsync(10, t => t);

            return files.ToArray();
        }

        public async Task<IEnumerable<EvacuationFile>> Read(EvacuationFilesQuery query)
        {
            var readCtx = essContext.Clone();
            readCtx.MergeOption = MergeOption.NoTracking;

            //get all matching files
            var files = (await QueryHouseholdMemberFiles(readCtx, query)).Concat(await QueryEvacuationFiles(readCtx, query)).Concat(await QueryNeedsAssessments(readCtx, query));

            //secondary filter after loading the files
            if (!string.IsNullOrEmpty(query.FileId)) files = files.Where(f => f.era_name == query.FileId);
            if (query.RegistraionDateFrom.HasValue) files = files.Where(f => f.createdon.Value.UtcDateTime >= query.RegistraionDateFrom.Value);
            if (query.RegistraionDateTo.HasValue) files = files.Where(f => f.createdon.Value.UtcDateTime <= query.RegistraionDateTo.Value);
            if (query.IncludeFilesInStatuses.Any()) files = files.Where(f => query.IncludeFilesInStatuses.Any(s => (int)s == f.era_essfilestatus));
            if (query.Limit.HasValue) files = files.OrderByDescending(f => f.era_name).Take(query.Limit.Value);

            //ensure files will be loaded only once and have a needs assessment
            files = files
                .Where(f => f.statecode == (int)EntityState.Active && f._era_currentneedsassessmentid_value.HasValue)
                .Distinct(new LambdaComparer<era_evacuationfile>((f1, f2) => f1.era_evacuationfileid == f2.era_evacuationfileid, f => f.era_evacuationfileid.GetHashCode()));

            return (await ParallelLoadEvacuationFilesAsync(essContext, files)).Select(f => MapEvacuationFile(f, query.MaskSecurityPhrase)).ToArray();
        }

        private static async Task<IEnumerable<era_evacuationfile>> QueryHouseholdMemberFiles(EssContext ctx, EvacuationFilesQuery query)
        {
            var shouldQueryHouseholdMembers =
                string.IsNullOrEmpty(query.FileId) && string.IsNullOrEmpty(query.NeedsAssessmentId) &&
             (!string.IsNullOrEmpty(query.LinkedRegistrantId) ||
             !string.IsNullOrEmpty(query.PrimaryRegistrantId) ||
             !string.IsNullOrEmpty(query.HouseholdMemberId));

            if (!shouldQueryHouseholdMembers) return Array.Empty<era_evacuationfile>();

            var memberQuery = ctx.era_householdmembers.Expand(m => m.era_EvacuationFileid).Where(m => m.statecode == (int)EntityState.Active);

            if (!string.IsNullOrEmpty(query.PrimaryRegistrantId)) memberQuery = memberQuery.Where(m => m.era_isprimaryregistrant == true && m._era_registrant_value == Guid.Parse(query.PrimaryRegistrantId));
            if (!string.IsNullOrEmpty(query.HouseholdMemberId)) memberQuery = memberQuery.Where(m => m.era_householdmemberid == Guid.Parse(query.HouseholdMemberId));
            if (!string.IsNullOrEmpty(query.LinkedRegistrantId)) memberQuery = memberQuery.Where(m => m._era_registrant_value == Guid.Parse(query.LinkedRegistrantId));

            return (await ((DataServiceQuery<era_householdmember>)memberQuery).GetAllPagesAsync()).Select(m => m.era_EvacuationFileid).ToArray();
        }

        private static async Task<IEnumerable<era_evacuationfile>> QueryEvacuationFiles(EssContext ctx, EvacuationFilesQuery query)
        {
            var shouldQueryFiles =
                string.IsNullOrEmpty(query.NeedsAssessmentId) &&
                (!string.IsNullOrEmpty(query.FileId) ||
                query.RegistraionDateFrom.HasValue ||
                query.RegistraionDateTo.HasValue);

            if (!shouldQueryFiles) return Array.Empty<era_evacuationfile>();

            var filesQuery = ctx.era_evacuationfiles.Expand(f => f.era_CurrentNeedsAssessmentid).Where(f => f.statecode == (int)EntityState.Active);

            if (!string.IsNullOrEmpty(query.FileId)) filesQuery = filesQuery.Where(f => f.era_name == query.FileId);
            if (query.RegistraionDateFrom.HasValue) filesQuery = filesQuery.Where(f => f.createdon >= query.RegistraionDateFrom.Value);
            if (query.RegistraionDateTo.HasValue) filesQuery = filesQuery.Where(f => f.createdon <= query.RegistraionDateTo.Value);

            return (await ((DataServiceQuery<era_evacuationfile>)filesQuery).GetAllPagesAsync()).ToArray();
        }

        private static async Task<IEnumerable<era_evacuationfile>> QueryNeedsAssessments(EssContext ctx, EvacuationFilesQuery query)
        {
            var shouldQueryNeedsAssessments = !string.IsNullOrEmpty(query.NeedsAssessmentId) && !string.IsNullOrEmpty(query.FileId);

            if (!shouldQueryNeedsAssessments) return Array.Empty<era_evacuationfile>();

            var needsAssessmentQuery = ctx.era_needassessments
               .Expand(na => na.era_EvacuationFile)
                        .Where(n => n.era_needassessmentid == Guid.Parse(query.NeedsAssessmentId));

            return (await ((DataServiceQuery<era_needassessment>)needsAssessmentQuery).GetAllPagesAsync()).Select(na =>
            {
                na.era_EvacuationFile.era_CurrentNeedsAssessmentid = na;
                return na.era_EvacuationFile;
            }).Where(f => f.era_name == query.FileId).ToArray();
        }

        public async Task<string> CreateNote(string fileId, Note note)
        {
            var file = essContext.era_evacuationfiles
                .Where(f => f.era_name == fileId).SingleOrDefault();
            if (file == null) throw new Exception($"Evacuation file {fileId} not found");

            var newNote = mapper.Map<era_essfilenote>(note);
            newNote.era_essfilenoteid = Guid.NewGuid();
            essContext.AddToera_essfilenotes(newNote);
            essContext.AddLink(file, nameof(era_evacuationfile.era_era_evacuationfile_era_essfilenote_ESSFileID), newNote);
            essContext.SetLink(newNote, nameof(newNote.era_ESSFileID), file);

            if (newNote._era_essteamuserid_value.HasValue)
            {
                var user = essContext.era_essteamusers.Where(u => u.era_essteamuserid == newNote._era_essteamuserid_value).SingleOrDefault();
                if (user != null) essContext.AddLink(user, nameof(era_essteamuser.era_era_essteamuser_era_essfilenote_ESSTeamUser), newNote);
            }
            await essContext.SaveChangesAsync();

            essContext.DetachAll();

            return newNote.era_essfilenoteid.ToString();
        }

        public async Task<string> UpdateNote(string fileId, Note note)
        {
            var existingNote = essContext.era_essfilenotes
                .Where(n => n.era_essfilenoteid == new Guid(note.Id)).SingleOrDefault();
            essContext.DetachAll();

            if (existingNote == null) throw new Exception($"Evacuation file note {note.Id} not found");

            var updatedNote = mapper.Map<era_essfilenote>(note);

            updatedNote.era_essfilenoteid = existingNote.era_essfilenoteid;
            essContext.AttachTo(nameof(EssContext.era_essfilenotes), updatedNote);
            essContext.UpdateObject(updatedNote);

            await essContext.SaveChangesAsync();

            essContext.DetachAll();

            return updatedNote.era_essfilenoteid.ToString();
        }

        public async Task<string> CreateSupport(string fileId, Support support)
        {
            var file = essContext.era_evacuationfiles.Expand(f => f.era_CurrentNeedsAssessmentid).Where(f => f.era_name == fileId).SingleOrDefault();
            if (file == null) throw new Exception($"Evacuation file {fileId} not found");

            var newSupport = mapper.Map<era_evacueesupport>(support);
            newSupport.era_evacueesupportid = Guid.NewGuid();

            essContext.AddToera_evacueesupports(newSupport);
            essContext.AddLink(file, nameof(era_evacuationfile.era_era_evacuationfile_era_evacueesupport_ESSFileId), newSupport);
            essContext.AddLink(file.era_CurrentNeedsAssessmentid, nameof(era_needassessment.era_era_needassessment_era_evacueesupport_NeedsAssessmentID), newSupport);
            essContext.SetLink(newSupport, nameof(era_evacueesupport.era_EvacuationFileId), file);
            essContext.SetLink(newSupport, nameof(era_evacueesupport.era_NeedsAssessmentID), file.era_CurrentNeedsAssessmentid);

            var teamMember = essContext.era_essteamusers.Where(tu => tu.era_essteamuserid == newSupport._era_issuedbyid_value).SingleOrDefault();
            essContext.SetLink(newSupport, nameof(era_evacueesupport.era_IssuedById), teamMember);

            AssignSupplierToSupport(newSupport);
            AssignHouseholdMembersToSupport(newSupport);

            await essContext.SaveChangesAsync();

            essContext.Detach(newSupport);

            //get the auto generated support id
            var supportId = essContext.era_evacueesupports
                .Where(s => s.era_evacueesupportid == newSupport.era_evacueesupportid)
                .Select(s => s.era_name)
                .Single();

            essContext.DetachAll();

            return supportId;
        }

        public async Task<string> UpdateSupport(string fileId, Support support)
        {
            var supports = essContext.era_evacueesupports
                .Expand(s => s.era_EvacuationFileId)
                .Where(s => s.era_name == support.Id).ToArray();

            var existingSupport = supports.Where(s => s.era_EvacuationFileId.era_name == fileId).SingleOrDefault();

            if (existingSupport == null) throw new Exception($"Support {support.Id} not found in file {fileId}");

            RemoveAllHouseholdMembersFromSupport(existingSupport);
            await essContext.SaveChangesAsync();
            essContext.DetachAll();

            var updatedSupport = mapper.Map<era_evacueesupport>(support);
            updatedSupport.era_evacueesupportid = existingSupport.era_evacueesupportid;

            essContext.AttachTo(nameof(EssContext.era_evacueesupports), updatedSupport);

            var teamMember = essContext.era_essteamusers.ByKey(updatedSupport._era_issuedbyid_value).GetValue();
            essContext.SetLink(updatedSupport, nameof(era_evacueesupport.era_IssuedById), teamMember);

            essContext.UpdateObject(updatedSupport);
            AssignHouseholdMembersToSupport(updatedSupport);
            AssignSupplierToSupport(updatedSupport);

            await essContext.SaveChangesAsync();

            essContext.DetachAll();

            return updatedSupport.era_name.ToString();
        }

        public async Task<string> VoidSupport(string fileId, string supportId, SupportVoidReason reason)
        {
            var supports = essContext.era_evacueesupports
                .Expand(s => s.era_EvacuationFileId)
                .Where(s => s.era_name == supportId).ToArray();

            var existingSupport = supports.Where(s => s.era_EvacuationFileId.era_name == fileId).SingleOrDefault();

            if (existingSupport != null)
            {
                existingSupport.era_voidreason = (int)reason;
                essContext.DeactivateObject(existingSupport, (int)SupportStatus.Void);
                await essContext.SaveChangesAsync();
            }
            essContext.DetachAll();

            return fileId;
        }

        private void AssignHouseholdMembersToSupport(era_evacueesupport support)
        {
            foreach (var member in support.era_era_householdmember_era_evacueesupport)
            {
                essContext.AttachTo(nameof(EssContext.era_householdmembers), member);
                essContext.AddLink(member, nameof(era_householdmember.era_era_householdmember_era_evacueesupport), support);
            }
        }

        private void RemoveAllHouseholdMembersFromSupport(era_evacueesupport support)
        {
            var householdMembers = essContext.LoadProperty(support, nameof(era_evacueesupport.era_era_householdmember_era_evacueesupport));
            foreach (var member in householdMembers)
            {
                essContext.DeleteLink(member, nameof(era_householdmember.era_era_householdmember_era_evacueesupport), support);
            }
        }

        private void AssignSupplierToSupport(era_evacueesupport support)
        {
            if (support._era_supplierid_value.HasValue)
            {
                var supplier = essContext.era_suppliers.Where(s => s.era_supplierid == support._era_supplierid_value).SingleOrDefault();
                if (supplier == null) throw new Exception($"Supplier id {support._era_supplierid_value} not found");
                essContext.SetLink(support, nameof(era_evacueesupport.era_SupplierId), supplier);
            }
        }
    }
}
