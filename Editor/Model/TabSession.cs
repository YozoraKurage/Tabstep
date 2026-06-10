using System;
using System.Collections.Generic;
using UnityEngine;

namespace Yozolab.Tabstep
{
    /// <summary>
    /// Folder tabs of an Tabstep window — see <see cref="TabCollection{TTab}"/>.
    /// On top of the generic tab-bar logic this adds the folder-tab policies:
    /// pinned tabs live in a leftmost region, and closed tabs are remembered so
    /// they can be reopened (Ctrl+Shift+T).
    /// </summary>
    [Serializable]
    class TabSession : TabCollection<TabState>
    {
        // Oldest entries are dropped past this; keeps serialized window state bounded.
        internal const int MaxClosedTabs = 10;

        [SerializeField] List<TabState> _recentlyClosed = new List<TabState>();

        public bool HasClosedTabs => _recentlyClosed.Count > 0;

        /// <summary>Most recently closed last; read-only peek for the start page.</summary>
        public IReadOnlyList<TabState> RecentlyClosed => _recentlyClosed;

        public TabState OpenTab(string folderPath, bool activate = true)
        {
            return Add(new TabState(folderPath), activate);
        }

        /// <summary>Opens a tab right after the active one (never inside the pinned region).</summary>
        public TabState OpenTabAfterActive(string folderPath)
        {
            return AddTab(new TabState(folderPath), besideActive: true);
        }

        /// <summary>
        /// Adds a prepared tab (possibly an empty start-page tab), either at the end of
        /// the bar or right after the active tab — never inside the pinned region.
        /// </summary>
        public TabState AddTab(TabState tab, bool besideActive)
        {
            if (!besideActive || ActiveIndex < 0) return Add(tab);
            return InsertAfter(Math.Max(ActiveIndex, PinnedCount - 1), tab);
        }

        /// <summary>Inserts a copy (including history) right after the source and activates it.</summary>
        public TabState DuplicateTab(int index)
        {
            if (index < 0 || index >= Count) return null;
            // A copy of a pinned tab is unpinned, so it must land outside the pinned region.
            return InsertAfter(Math.Max(index, PinnedCount - 1), Tabs[index].Clone());
        }

        /// <summary>Number of leading pinned tabs — the pinned region is [0, PinnedCount).</summary>
        public int PinnedCount
        {
            get
            {
                int n = 0;
                while (n < Count && Tabs[n].Pinned) n++;
                return n;
            }
        }

        /// <summary>
        /// Pins or unpins a tab, moving it to the matching edge of the pinned region
        /// so pinned tabs always stay leftmost.
        /// </summary>
        public void SetPinned(int index, bool pinned)
        {
            if (index < 0 || index >= Count) return;
            var tab = Tabs[index];
            if (tab.Pinned == pinned) return;
            int boundary = PinnedCount;
            tab.Pinned = pinned;
            Move(index, pinned ? boundary : boundary - 1);
        }

        /// <summary>
        /// Drag-reorder constrained to the tab's own region (pinned stay among pinned).
        /// Returns the index the tab ended up at, or -1 for an invalid <paramref name="from"/>.
        /// </summary>
        public int MoveTab(int from, int to)
        {
            if (from < 0 || from >= Count) return -1;
            int pinned = PinnedCount;
            to = Tabs[from].Pinned
                ? Math.Clamp(to, 0, pinned - 1)
                : Math.Clamp(to, pinned, Count - 1);
            Move(from, to);
            return to;
        }

        /// <summary>Reopens the most recently closed tab (history included) as the active last tab.</summary>
        public TabState ReopenClosedTab()
        {
            if (_recentlyClosed.Count == 0) return null;
            var tab = _recentlyClosed[_recentlyClosed.Count - 1];
            _recentlyClosed.RemoveAt(_recentlyClosed.Count - 1);
            // Appending at the end — the pin (a slot property) does not come back with it.
            tab.Pinned = false;
            return Add(tab);
        }

        protected override bool IsCloseExempt(TabState tab) => tab.Pinned;

        protected override void OnTabClosed(TabState tab)
        {
            _recentlyClosed.Add(tab);
            if (_recentlyClosed.Count > MaxClosedTabs)
                _recentlyClosed.RemoveAt(0);
        }
    }
}
