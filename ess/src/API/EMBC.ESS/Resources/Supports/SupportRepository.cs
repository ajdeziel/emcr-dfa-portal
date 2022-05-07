﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using EMBC.ESS.Utilities.Dynamics;
using EMBC.ESS.Utilities.Dynamics.Microsoft.Dynamics.CRM;
using Microsoft.OData.Client;

namespace EMBC.ESS.Resources.Supports
{
    public class SupportRepository : ISupportRepository
    {
        private readonly IEssContextFactory essContextFactory;
        private readonly IMapper mapper;

        private static CancellationToken CreateCancellationToken() => new CancellationTokenSource().Token;

        public SupportRepository(IEssContextFactory essContextFactory, IMapper mapper)
        {
            this.essContextFactory = essContextFactory;
            this.mapper = mapper;
        }

        public async Task<ManageSupportCommandResult> Manage(ManageSupportCommand cmd)
        {
            var ct = CreateCancellationToken();
            return cmd switch
            {
                SaveEvacuationFileSupportCommand c => await Handle(c, ct),
                ChangeSupportStatusCommand c => await Handle(c, ct),
                SubmitSupportForApprovalCommand c => await Handle(c, ct),

                _ => throw new NotSupportedException($"{cmd.GetType().Name} is not supported")
            };
        }

        public async Task<SupportQueryResult> Query(SupportQuery query)
        {
            var ct = CreateCancellationToken();
            return query switch
            {
                SearchSupportsQuery q => await Handle(q, ct),

                _ => throw new NotSupportedException($"{query.GetType().Name} is not supported")
            };
        }

        private static readonly Guid ApprovalQueueId = new("a4f0fbbe-89a1-ec11-b831-00505683fbf4");
        private static readonly Guid ReviewQueueId = new("e969aae7-8aa1-ec11-b831-00505683fbf4");

        private async Task<AssignSupportToQueueCommandResult> Handle(SubmitSupportForApprovalCommand cmd, CancellationToken ct)
        {
            var ctx = essContextFactory.Create();

            var support = (await ((DataServiceQuery<era_evacueesupport>)ctx.era_evacueesupports.Where(s => s.era_name == cmd.SupportId)).GetAllPagesAsync()).SingleOrDefault();
            if (support == null) throw new InvalidOperationException($"Support {cmd.SupportId} not found");

            var flagTypes = (await ctx.era_supportflagtypes.GetAllPagesAsync()).ToArray();
            foreach (var flag in cmd.Flags)
            {
                var supportFlag = mapper.Map<era_supportflag>(flag);
                ctx.AddToera_supportflags(supportFlag);
                ctx.SetLink(supportFlag, nameof(era_supportflag.era_FlagType), flagTypes.Single(t => t.era_supportflagtypeid == supportFlag._era_flagtype_value));
                ctx.SetLink(supportFlag, nameof(era_supportflag.era_EvacueeSupport), support);
                if (flag is DuplicateSupportFlag dup)
                {
                    var duplicateSupport = (await ((DataServiceQuery<era_evacueesupport>)ctx.era_evacueesupports.Where(s => s.era_name == dup.DuplicatedSupportId))
                        .GetAllPagesAsync()).SingleOrDefault();
                    if (duplicateSupport == null) throw new InvalidOperationException($"Support {dup.DuplicatedSupportId} not found");
                    ctx.SetLink(supportFlag, nameof(era_supportflag.era_SupportDuplicate), duplicateSupport);
                }
            }
            var queues = (await ctx.queues.GetAllPagesAsync()).ToArray();

            var queue = queues.Single(q => q.queueid == (cmd.Flags.Any() ? ReviewQueueId : ApprovalQueueId));
            var queueItem = new queueitem
            {
                queueitemid = Guid.NewGuid(),
                objecttypecode = 10056 //support type picklist value
            };

            // create queue item
            ctx.AddToqueueitems(queueItem);
            ctx.SetLink(queueItem, nameof(queueItem.queueid), queue);
            ctx.SetLink(queueItem, nameof(queueItem.objectid_era_evacueesupport), support);

            // update support status
            support.statuscode = (int)SupportStatus.PendingApproval;
            ctx.UpdateObject(support);

            await ctx.SaveChangesAsync();

            ctx.DetachAll();

            return new AssignSupportToQueueCommandResult();
        }

        private async Task<SaveEvacuationFileSupportCommandResult> Handle(SaveEvacuationFileSupportCommand cmd, CancellationToken ct)
        {
            var ctx = essContextFactory.Create();
            var file = (await ((DataServiceQuery<era_evacuationfile>)ctx.era_evacuationfiles
                .Expand(f => f.era_CurrentNeedsAssessmentid)
                .Where(f => f.era_name == cmd.FileId))
                .ExecuteAsync(ct))
                .SingleOrDefault();

            if (file == null) throw new ArgumentException($"Evacuation file {cmd.FileId} not found");

            var mappedSupports = mapper.Map<IEnumerable<era_evacueesupport>>(cmd.Supports).ToArray();

            foreach (var support in mappedSupports)
            {
                if (support.era_name == null)
                {
                    await CreateSupport(ctx, file, support, ct);
                }
                else
                {
                    await UpdateSupport(ctx, file, support, ct);
                }
            }

            await ctx.SaveChangesAsync(ct);

            ctx.DetachAll();

            await Parallel.ForEachAsync(mappedSupports.Where(s => s.era_name == null), ct,
                async (s, ct) => s.era_name = await ctx.era_evacueesupports.ByKey(s.era_evacueesupportid).Select(s => s.era_name).GetValueAsync(ct));

            ctx.DetachAll();

            return new SaveEvacuationFileSupportCommandResult { Supports = mapper.Map<IEnumerable<Support>>(mappedSupports) };
        }

        private async Task<ManageSupportCommandResult> Handle(ChangeSupportStatusCommand cmd, CancellationToken ct)
        {
            var ctx = essContextFactory.Create();
            var changesSupportIds = new List<string>();
            foreach (var item in cmd.Items)
            {
                var support = (await ((DataServiceQuery<era_evacueesupport>)ctx.era_evacueesupports.Where(s => s.era_name == item.SupportId)).GetAllPagesAsync()).SingleOrDefault();
                if (support == null) throw new InvalidOperationException($"Support {item.SupportId} not found, can't update its status");
                ChangeSupportStatus(ctx, support, item.ToStatus, item.Reason);
                ctx.UpdateObject(support);
                changesSupportIds.Add(item.SupportId);
            }

            await ctx.SaveChangesAsync(ct);
            ctx.DetachAll();
            return new ChangeSupportStatusCommandResult { Ids = changesSupportIds.ToArray() };
        }

        private async Task<SearchSupportQueryResult> Handle(SearchSupportsQuery query, CancellationToken ct)
        {
            var ctx = essContextFactory.CreateReadOnly();
            var supports = (await Search(ctx, query, ct)).ToArray();
            await Parallel.ForEachAsync(supports, ct, async (s, ct) => await LoadSupportDetails(ctx, s, ct));

            var results = new SearchSupportQueryResult { Items = mapper.Map<IEnumerable<Support>>(supports).ToArray() };

            ctx.DetachAll();

            return results;
        }

        private static async Task<IEnumerable<era_evacueesupport>> Search(EssContext ctx, SearchSupportsQuery query, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(query.ById) &&
                string.IsNullOrEmpty(query.ByManualReferralId) &&
                string.IsNullOrEmpty(query.ByEvacuationFileId) &&
                !query.ByStatus.HasValue)
                throw new ArgumentException("Supports query must have at least one criteria", nameof(query));

            // search a specific file
            if (!string.IsNullOrEmpty(query.ByEvacuationFileId))
            {
                var file = (await ((DataServiceQuery<era_evacuationfile>)ctx.era_evacuationfiles
                    .Where(f => f.era_name == query.ByEvacuationFileId))
                    .ExecuteAsync(ct))
                    .SingleOrDefault();
                if (file == null) return Array.Empty<era_evacueesupport>();

                ctx.AttachTo(nameof(EssContext.era_evacuationfiles), file);
                await ctx.LoadPropertyAsync(file, nameof(era_evacuationfile.era_era_evacuationfile_era_evacueesupport_ESSFileId), ct);
                IEnumerable<era_evacueesupport> supports = file.era_era_evacuationfile_era_evacueesupport_ESSFileId;
                if (!string.IsNullOrEmpty(query.ById)) supports = supports.Where(s => s.era_name == query.ById);
                if (!string.IsNullOrEmpty(query.ByManualReferralId)) supports = supports.Where(s => s.era_manualsupport == query.ByManualReferralId);
                supports = supports.OrderBy(s => s.createdon);

                return supports;
            }

            // search all supports
            IQueryable<era_evacueesupport> supportsQuery = ctx.era_evacueesupports;

            if (!string.IsNullOrEmpty(query.ById)) supportsQuery = supportsQuery.Where(s => s.era_name == query.ById);
            if (!string.IsNullOrEmpty(query.ByManualReferralId)) supportsQuery = supportsQuery.Where(s => s.era_manualsupport == query.ByManualReferralId);
            if (query.ByStatus.HasValue) supportsQuery = supportsQuery.Where(s => s.statuscode == (int)query.ByStatus.Value);
            supportsQuery = supportsQuery.OrderBy(s => s.createdon);
            if (query.LimitNumberOfResults.HasValue) supportsQuery = supportsQuery.Take(query.LimitNumberOfResults.Value);

            return await ((DataServiceQuery<era_evacueesupport>)supportsQuery).GetAllPagesAsync(ct);
        }

        private static async Task LoadSupportDetails(EssContext ctx, era_evacueesupport support, CancellationToken ct)
        {
            ctx.AttachTo(nameof(EssContext.era_evacueesupports), support);
            var tasks = new List<Task>();

            tasks.Add(ctx.LoadPropertyAsync(support, nameof(era_evacueesupport.era_EvacuationFileId), ct));
            tasks.Add(ctx.LoadPropertyAsync(support, nameof(era_evacueesupport.era_era_householdmember_era_evacueesupport), ct));
            tasks.Add(ctx.LoadPropertyAsync(support, nameof(era_evacueesupport.era_era_evacueesupport_era_supportflag_EvacueeSupport), ct));
            tasks.Add(ctx.LoadPropertyAsync(support, nameof(era_evacueesupport.era_era_etransfertransaction_era_evacueesuppo), ct));

            await Task.WhenAll(tasks);

            foreach (var flag in support.era_era_evacueesupport_era_supportflag_EvacueeSupport)
            {
                if (flag._era_supportduplicate_value.HasValue)
                {
                    ctx.AttachTo(nameof(EssContext.era_supportflags), flag);
                    await ctx.LoadPropertyAsync(flag, nameof(era_supportflag.era_SupportDuplicate), ct);
                }
            }
        }

        private static async Task CreateSupport(EssContext ctx, era_evacuationfile file, era_evacueesupport support, CancellationToken ct)
        {
            support.era_evacueesupportid = Guid.NewGuid();

            ctx.AddToera_evacueesupports(support);
            ctx.AddLink(file, nameof(era_evacuationfile.era_era_evacuationfile_era_evacueesupport_ESSFileId), support);
            ctx.AddLink(file.era_CurrentNeedsAssessmentid, nameof(era_needassessment.era_era_needassessment_era_evacueesupport_NeedsAssessmentID), support);
            ctx.SetLink(support, nameof(era_evacueesupport.era_EvacuationFileId), file);
            ctx.SetLink(support, nameof(era_evacueesupport.era_NeedsAssessmentID), file.era_CurrentNeedsAssessmentid);
            if (support._era_grouplodgingcityid_value.HasValue)
                ctx.SetLink(support, nameof(era_evacueesupport.era_GroupLodgingCityID), ctx.LookupJurisdictionByCode(support._era_grouplodgingcityid_value?.ToString()));

            var teamMember = await ctx.era_essteamusers.ByKey(support._era_issuedbyid_value).GetValueAsync(ct);
            if (teamMember == null) throw new InvalidOperationException($"team member {support._era_issuedbyid_value} not found");
            ctx.SetLink(support, nameof(era_evacueesupport.era_IssuedById), teamMember);

            AssignHouseholdMembersToSupport(ctx, support, support.era_era_householdmember_era_evacueesupport);
            AssignSupplierToSupport(ctx, support);
            AssignETransferRecipientToSupport(ctx, support);
        }

        private static async Task UpdateSupport(EssContext ctx, era_evacuationfile file, era_evacueesupport support, CancellationToken ct)
        {
            var existingSupport = (await ((DataServiceQuery<era_evacueesupport>)ctx.era_evacueesupports
                .Where(s => s.era_name == support.era_name))
                .ExecuteAsync(ct))
                .SingleOrDefault();

            if (existingSupport == null) throw new ArgumentException($"Support {support.era_name} not found");
            if (existingSupport._era_evacuationfileid_value != file.era_evacuationfileid)
                throw new InvalidOperationException($"Support {support.era_name} not found in file {file.era_name}");

            await ctx.LoadPropertyAsync(existingSupport, nameof(era_evacueesupport.era_era_householdmember_era_evacueesupport), ct);
            var currentHouseholdMembers = existingSupport.era_era_householdmember_era_evacueesupport.ToArray();

            ctx.Detach(existingSupport);
            // foreach (var member in existingSupport.era_era_householdmember_era_evacueesupport) essContext.Detach(member);

            support.era_evacueesupportid = existingSupport.era_evacueesupportid;
            ctx.AttachTo(nameof(EssContext.era_evacueesupports), support);
            ctx.SetLink(support, nameof(era_evacueesupport.era_GroupLodgingCityID), ctx.LookupJurisdictionByCode(support._era_grouplodgingcityid_value?.ToString()));

            var teamMember = ctx.era_essteamusers.ByKey(support._era_issuedbyid_value).GetValue();
            ctx.SetLink(support, nameof(era_evacueesupport.era_IssuedById), teamMember);

            ctx.UpdateObject(support);
            // remove household members no longer part of the support
            RemoveHouseholdMembersFromSupport(ctx, support, currentHouseholdMembers.Where(m => !support.era_era_householdmember_era_evacueesupport.Any(im => im.era_householdmemberid == m.era_householdmemberid)));
            // add household members to support
            AssignHouseholdMembersToSupport(ctx, support, support.era_era_householdmember_era_evacueesupport.Where(m => !currentHouseholdMembers.Any(im => im.era_householdmemberid == m.era_householdmemberid)));
            AssignSupplierToSupport(ctx, support);
            AssignETransferRecipientToSupport(ctx, support);
        }

        private static void ChangeSupportStatus(EssContext ctx, era_evacueesupport support, SupportStatus status, string changeReason)
        {
            var supportDeliveryType = (SupportMethod)support.era_supportdeliverytype;

            switch (status)
            {
                case SupportStatus.Void when supportDeliveryType == SupportMethod.Referral:
                    support.era_voidreason = (int)Enum.Parse<SupportVoidReason>(changeReason);
                    ctx.DeactivateObject(support, (int)status);
                    break;

                case SupportStatus.Cancelled when supportDeliveryType == SupportMethod.ETransfer:
                    ctx.DeactivateObject(support, (int)status);
                    break;

                default:
                    support.statuscode = (int)status;
                    break;
            }
        }

        private static void AssignHouseholdMembersToSupport(EssContext ctx, era_evacueesupport support, IEnumerable<era_householdmember> householdMembers)
        {
            foreach (var member in householdMembers)
            {
                ctx.AddLink(member, nameof(era_householdmember.era_era_householdmember_era_evacueesupport), support);
            }
        }

        private static void RemoveHouseholdMembersFromSupport(EssContext essContext, era_evacueesupport support, IEnumerable<era_householdmember> householdMembers)
        {
            foreach (var member in householdMembers)
            {
                essContext.DeleteLink(member, nameof(era_householdmember.era_era_householdmember_era_evacueesupport), support);
            }
        }

        private static void AssignSupplierToSupport(EssContext ctx, era_evacueesupport support)
        {
            if (support._era_supplierid_value.HasValue)
            {
                var supplier = ctx.era_suppliers.Where(s => s.era_supplierid == support._era_supplierid_value && s.statecode == (int)EntityState.Active).SingleOrDefault();
                if (supplier == null) throw new ArgumentException($"Supplier id {support._era_supplierid_value} not found or is not active");
                ctx.SetLink(support, nameof(era_evacueesupport.era_SupplierId), supplier);
            }
        }

        private static void AssignETransferRecipientToSupport(EssContext ctx, era_evacueesupport support)
        {
            if (support._era_payeeid_value.HasValue)
            {
                var registrant = ctx.contacts.Where(s => s.contactid == support._era_payeeid_value && s.statecode == (int)EntityState.Active).SingleOrDefault();
                if (registrant == null) throw new ArgumentException($"Registrant id {support._era_payeeid_value} not found or is not active");
                ctx.SetLink(support, nameof(era_evacueesupport.era_PayeeId), registrant);
            }
        }
    }
}
