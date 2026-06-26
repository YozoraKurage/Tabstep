using System.Collections.Generic;
using UnityEditor;
using UnityEditor.ProjectWindowCallback;
using UnityEngine;

namespace Yozolab.Tabstep
{
    /// <summary>
    /// Routes a freshly invoked Assets/Create/... request from Unity into the right
    /// <see cref="AssetColumnView"/>. Unity hands the request to the last-interacted
    /// Project browser via <c>ProjectBrowser.BeginPreimportedNameEditing</c>; that
    /// browser would then begin inline-renaming the new asset inside its own list
    /// area — which the column view covers, so the rename text field never reaches
    /// the user. <see cref="ProjectBrowserPatcher"/> intercepts the call (Harmony
    /// prefix) and deposits the request here; the column view drains it on its next
    /// event pass and drives the rename itself, finally invoking
    /// <see cref="EndNameEditAction.Action"/> / <see cref="EndNameEditAction.Cancelled"/>
    /// to actually create or discard the asset.
    /// </summary>
    internal static class AssetCreationBridge
    {
        /// <summary>A captured request, owned by a single host until it is drained.</summary>
        public sealed class Request
        {
            public int InstanceID;
            public EndNameEditAction EndAction;
            public string PathName;
            public Texture2D Icon;
            public string ResourceFile;
        }

        // One request per owning host window at a time — the second submission
        // cancels the first the same way the stock browser does when a new create
        // arrives mid-edit.
        static readonly Dictionary<EditorWindow, Request> _pending =
            new Dictionary<EditorWindow, Request>();

        /// <summary>Deposits <paramref name="request"/> for <paramref name="owner"/> to drain.</summary>
        public static void Submit(EditorWindow owner, Request request)
        {
            if (owner == null || request == null) return;
            // A still-pending earlier request is dropped — Unity's stock flow never
            // stacks two preimported edits either.
            _pending[owner] = request;
        }

        /// <summary>Removes and returns the request waiting for <paramref name="owner"/>, or null.</summary>
        public static Request Take(EditorWindow owner)
        {
            if (owner == null) return null;
            if (!_pending.TryGetValue(owner, out var r)) return null;
            _pending.Remove(owner);
            return r;
        }

        /// <summary>Drops any pending request for <paramref name="owner"/> without invoking it.</summary>
        public static void Discard(EditorWindow owner)
        {
            if (owner == null) return;
            _pending.Remove(owner);
        }
    }
}
