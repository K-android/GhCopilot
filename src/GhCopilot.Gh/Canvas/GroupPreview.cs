using System;
using System.Collections.Generic;
using System.Drawing;
using GhCopilot.Core;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;

namespace GhCopilot.Gh.Canvas
{
    public sealed class PendingExternalRewire
    {
        public PendingExternalRewire(
            Guid oldSourceNodeGuid,
            string oldSourceParamName,
            Guid newSourceNodeGuid,
            string newSourceParamName,
            Guid externalRecipientNodeGuid,
            string externalRecipientParamName)
        {
            OldSourceNodeGuid = oldSourceNodeGuid;
            OldSourceParamName = oldSourceParamName;
            NewSourceNodeGuid = newSourceNodeGuid;
            NewSourceParamName = newSourceParamName;
            ExternalRecipientNodeGuid = externalRecipientNodeGuid;
            ExternalRecipientParamName = externalRecipientParamName;
        }

        public Guid OldSourceNodeGuid { get; }
        public string OldSourceParamName { get; }
        public Guid NewSourceNodeGuid { get; }
        public string NewSourceParamName { get; }
        public Guid ExternalRecipientNodeGuid { get; }
        public string ExternalRecipientParamName { get; }
    }

    /// <summary>
    /// Tracks all objects created in a single AI preview run (plus optional original objects and external boundary rewires)
    /// so Accept and Reject operate atomically with clean single-step undo.
    /// </summary>
    public sealed class GroupPreview
    {
        public static readonly Color PreviewColour = Color.FromArgb(150, 255, 180, 40);
        public static readonly Color AcceptedColour = Color.FromArgb(110, 70, 190, 110);

        public GroupPreview(
            Guid groupInstanceGuid,
            IReadOnlyList<Guid> nodeInstanceGuids,
            IReadOnlyList<Guid>? replacedOriginalGuids = null,
            IReadOnlyList<PendingExternalRewire>? externalRewires = null)
        {
            GroupInstanceGuid = groupInstanceGuid;
            NodeInstanceGuids = nodeInstanceGuids ?? throw new ArgumentNullException(nameof(nodeInstanceGuids));
            ReplacedOriginalGuids = replacedOriginalGuids ?? Array.Empty<Guid>();
            ExternalRewires = externalRewires ?? Array.Empty<PendingExternalRewire>();
            IsActive = true;
        }

        public Guid GroupInstanceGuid { get; }

        public IReadOnlyList<Guid> NodeInstanceGuids { get; }

        public IReadOnlyList<Guid> ReplacedOriginalGuids { get; }

        public IReadOnlyList<PendingExternalRewire> ExternalRewires { get; }

        public bool IsActive { get; private set; }

        /// <summary>
        /// Keeps all spawned objects, re-attaches external downstream recipients, removes replaced original nodes,
        /// and recolours the preview group in one undo step.
        /// </summary>
        public bool Accept(GH_Document doc)
        {
            if (doc is null)
            {
                throw new ArgumentNullException(nameof(doc));
            }

            if (!IsActive)
            {
                return false;
            }

            int undoRecordsCreated = 0;

            // Rewire downstream recipients from replaced original outputs to new preview outputs before deletion.
            var rewiredParams = new List<IGH_Param>();
            foreach (PendingExternalRewire rewire in ExternalRewires)
            {
                IGH_DocumentObject? oldSrcObj = doc.FindObject(rewire.OldSourceNodeGuid, true);
                IGH_DocumentObject? newSrcObj = doc.FindObject(rewire.NewSourceNodeGuid, true);
                IGH_DocumentObject? targetObj = doc.FindObject(rewire.ExternalRecipientNodeGuid, true);

                if (oldSrcObj is null || newSrcObj is null || targetObj is null)
                {
                    continue;
                }

                IGH_Param? oldSrcParam = WireConnector.ResolveOutputParam(oldSrcObj, rewire.OldSourceParamName, out _);
                IGH_Param? newSrcParam = WireConnector.ResolveOutputParam(newSrcObj, rewire.NewSourceParamName, out _);
                IGH_Param? targetParam = WireConnector.ResolveInputParam(targetObj, rewire.ExternalRecipientParamName, out _);

                if (oldSrcParam != null && newSrcParam != null && targetParam != null)
                {
                    targetParam.ReplaceSource(oldSrcParam, newSrcParam);
                    rewiredParams.Add(targetParam);
                }
            }

            if (rewiredParams.Count > 0)
            {
                doc.UndoUtil.RecordWireEvent("GH Copilot: Rewire Downstream Outputs", rewiredParams);
                undoRecordsCreated++;
            }

            var originalsToRemove = new List<IGH_DocumentObject>();
            foreach (Guid origGuid in ReplacedOriginalGuids)
            {
                IGH_DocumentObject? orig = doc.FindObject(origGuid, true);
                if (orig != null)
                {
                    originalsToRemove.Add(orig);
                }
            }

            if (originalsToRemove.Count > 0)
            {
                doc.UndoUtil.RecordRemoveObjectEvent("GH Copilot: Remove Replaced Originals", originalsToRemove);
                doc.RemoveObjects(originalsToRemove, false);
                undoRecordsCreated++;
            }

            GH_Group? group = doc.FindObject(GroupInstanceGuid, true) as GH_Group;
            if (group != null)
            {
                doc.UndoUtil.RecordGenericObjectEvent("GH Copilot: Accept Preview", group);
                group.Colour = AcceptedColour;
                group.NickName = PluginConstants.ProductName;
                group.ExpireCaches();
                undoRecordsCreated++;
            }

            if (undoRecordsCreated > 1)
            {
                doc.UndoUtil.MergeRecords(undoRecordsCreated);
            }

            if (originalsToRemove.Count > 0 || rewiredParams.Count > 0)
            {
                doc.NewSolution(false);
            }

            IsActive = false;
            Instances.ActiveCanvas?.Invalidate();
            return true;
        }

        /// <summary>
        /// Removes every spawned object and the preview group from the document in a single undo step.
        /// Never deletes ReplacedOriginalGuids or modifies external wires.
        /// </summary>
        public bool Reject(GH_Document doc)
        {
            if (doc is null)
            {
                throw new ArgumentNullException(nameof(doc));
            }

            if (!IsActive)
            {
                return false;
            }

            var toRemove = new List<IGH_DocumentObject>();

            foreach (Guid nodeGuid in NodeInstanceGuids)
            {
                IGH_DocumentObject? obj = doc.FindObject(nodeGuid, true);
                if (obj != null)
                {
                    toRemove.Add(obj);
                }
            }

            IGH_DocumentObject? groupObj = doc.FindObject(GroupInstanceGuid, true);
            if (groupObj != null)
            {
                toRemove.Add(groupObj);
            }

            if (toRemove.Count > 0)
            {
                doc.UndoUtil.RecordRemoveObjectEvent("GH Copilot: Reject Preview", toRemove);
                doc.RemoveObjects(toRemove, false);
                doc.NewSolution(false);
            }

            IsActive = false;
            Instances.ActiveCanvas?.Invalidate();
            return true;
        }
    }
}
