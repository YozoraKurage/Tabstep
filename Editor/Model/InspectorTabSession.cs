// Parked: the tabbed inspector is temporarily withdrawn from the Unity UI while its
// integration strategy is reworked in a separate scope. Add TABSTEP_INSPECTOR to the
// project's Scripting Define Symbols to bring it back.
#if TABSTEP_INSPECTOR
using System;
using Object = UnityEngine.Object;

namespace Yozolab.Tabstep
{
    /// <summary>Object tabs of an Tabstep Inspector window — see <see cref="TabCollection{TTab}"/>.</summary>
    [Serializable]
    internal sealed class InspectorTabSession : TabCollection<InspectorTab>
    {
        public InspectorTab OpenTab(Object target, bool activate = true)
        {
            return Add(new InspectorTab(target), activate);
        }

        /// <summary>
        /// Activates the existing tab for <paramref name="target"/> when there is one,
        /// otherwise opens a new tab — keeps repeated double-clicks from flooding the bar.
        /// The Selection tab never counts as "existing": selecting an object before
        /// double-clicking it would otherwise swallow the new locked tab.
        /// </summary>
        public InspectorTab OpenOrFocusTab(Object target)
        {
            if (target == null) return null;
            for (int i = 0; i < Count; i++)
            {
                if (Tabs[i].FollowsSelection || Tabs[i].Target != target) continue;
                Activate(i);
                return Tabs[i];
            }
            return OpenTab(target);
        }

        public int SelectionTabIndex
        {
            get
            {
                for (int i = 0; i < Count; i++)
                    if (Tabs[i].FollowsSelection)
                        return i;
                return -1;
            }
        }

        /// <summary>Activates the pinned Selection tab, creating it (leftmost) when missing.</summary>
        public InspectorTab EnsureSelectionTab()
        {
            int index = SelectionTabIndex;
            if (index >= 0)
            {
                Activate(index);
                return Tabs[index];
            }
            return InsertFirst(InspectorTab.CreateSelectionTab());
        }

        public bool CloseSelectionTab()
        {
            int index = SelectionTabIndex;
            return index >= 0 && CloseTab(index);
        }

        public InspectorTab DuplicateTab(int index)
        {
            if (index < 0 || index >= Count) return null;
            return InsertAfter(index, Tabs[index].Clone());
        }
    }
}
#endif
