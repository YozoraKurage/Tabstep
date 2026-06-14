using System;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Yozolab.Tabstep
{
    /// <summary>
    /// One object parked on the Tabstep Shelf. Stored as a GlobalObjectId (plus the
    /// asset GUID when it is an asset) instead of a raw Object reference, so items
    /// survive domain reloads and identify sub-assets and scene objects precisely;
    /// scene objects resolve only while their scene is open. Whether an item came
    /// from the Project (an asset) or from a scene is recorded explicitly, and a
    /// locked item is kept on the shelf for the rest of the editor session.
    /// </summary>
    [Serializable]
    internal sealed class ShelfItem
    {
        /// <summary>Where the object lives — distinguished when the item is created.</summary>
        public enum SourceKind
        {
            Asset,
            Scene,
        }

        [SerializeField] string _globalId = "";
        [SerializeField] string _guid = "";
        [SerializeField] string _displayName = "";
        [SerializeField] SourceKind _kind = SourceKind.Asset;
        [SerializeField] bool _locked;

        [NonSerialized] Object _cached;

        ShelfItem() { }

        /// <summary>Null when the object cannot be identified (destroyed, not yet persisted...).</summary>
        public static ShelfItem ForObject(Object obj)
        {
            if (obj == null) return null;
            var item = new ShelfItem { _displayName = obj.name };
            var path = AssetDatabase.GetAssetPath(obj);
            if (!string.IsNullOrEmpty(path))
                item._guid = AssetDatabase.AssetPathToGUID(path);
            var globalId = GlobalObjectId.GetGlobalObjectIdSlow(obj);
            // identifierType 0 is the null id — a transient object that could never
            // be resolved again has no business on the shelf.
            if (globalId.identifierType == 0 && string.IsNullOrEmpty(item._guid)) return null;
            item._globalId = globalId.ToString();
            item._kind = string.IsNullOrEmpty(item._guid) ? SourceKind.Scene : SourceKind.Asset;
            item._cached = obj;
            return item;
        }

        /// <summary>Stable identity for dedup and for tracking an item through a drag.</summary>
        public string Key => string.IsNullOrEmpty(_globalId) ? _guid : _globalId;

        public SourceKind Kind => _kind;

        public bool IsAsset => _kind == SourceKind.Asset;

        public bool IsSceneObject => _kind == SourceKind.Scene;

        /// <summary>
        /// A locked item survives one-shot drag-outs and "Clear", and is restored for
        /// the rest of the editor session even if the shelf window is closed.
        /// </summary>
        public bool Locked
        {
            get => _locked;
            set => _locked = value;
        }

        /// <summary>Project path for assets (feeds DragAndDrop.paths), null for scene objects.</summary>
        public string AssetPath
        {
            get
            {
                if (!IsAsset || string.IsNullOrEmpty(_guid)) return null;
                var path = AssetDatabase.GUIDToAssetPath(_guid);
                return string.IsNullOrEmpty(path) ? null : path;
            }
        }

        public string DisplayName
        {
            get
            {
                var obj = Resolve();
                return obj != null ? obj.name : _displayName;
            }
        }

        /// <summary>The live object, or null when it no longer exists (shown greyed out).</summary>
        public Object Resolve()
        {
            if (_cached != null) return _cached;
            if (GlobalObjectId.TryParse(_globalId, out var id))
                _cached = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id);
            if (_cached == null && IsAsset)
            {
                var path = AssetPath;
                if (path != null) _cached = AssetDatabase.LoadMainAssetAtPath(path);
            }
            return _cached;
        }
    }
}
