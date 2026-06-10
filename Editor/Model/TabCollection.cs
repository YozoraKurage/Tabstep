using System;
using System.Collections.Generic;
using UnityEngine;

namespace Yozolab.Tabstep
{
    /// <summary>
    /// The ordered set of tabs in one window and which of them is active.
    /// Pure tab-bar logic (activate / close / cycle) shared by the folder tabs of
    /// Tabstep and the object tabs of Tabstep Inspector; serialized so tabs
    /// survive domain reloads.
    /// </summary>
    [Serializable]
    class TabCollection<TTab> where TTab : class
    {
        [SerializeField] List<TTab> _tabs = new List<TTab>();
        [SerializeField] int _activeIndex = -1;

        public IReadOnlyList<TTab> Tabs => _tabs;
        public int Count => _tabs.Count;

        public int ActiveIndex => _activeIndex;
        public TTab ActiveTab => _activeIndex >= 0 && _activeIndex < _tabs.Count ? _tabs[_activeIndex] : null;

        public TTab Add(TTab tab, bool activate = true)
        {
            _tabs.Add(tab);
            if (activate || _activeIndex < 0) _activeIndex = _tabs.Count - 1;
            return tab;
        }

        /// <summary>Inserts right after <paramref name="index"/> and activates (tab duplication).</summary>
        public TTab InsertAfter(int index, TTab tab)
        {
            if (index < 0 || index >= _tabs.Count || tab == null) return null;
            _tabs.Insert(index + 1, tab);
            _activeIndex = index + 1;
            return tab;
        }

        /// <summary>Inserts as the leftmost tab (used for pinned tabs).</summary>
        public TTab InsertFirst(TTab tab, bool activate = true)
        {
            if (tab == null) return null;
            _tabs.Insert(0, tab);
            if (activate || _activeIndex < 0) _activeIndex = 0;
            else _activeIndex++;
            return tab;
        }

        public void Activate(int index)
        {
            if (index >= 0 && index < _tabs.Count) _activeIndex = index;
        }

        /// <summary>Ctrl+Tab style cycling; wraps around both ends.</summary>
        public void CycleActive(int delta)
        {
            if (_tabs.Count == 0) return;
            _activeIndex = ((_activeIndex + delta) % _tabs.Count + _tabs.Count) % _tabs.Count;
        }

        /// <summary>Moves a tab to another position (drag reorder); the active tab stays active.</summary>
        public bool Move(int from, int to)
        {
            if (from < 0 || from >= _tabs.Count || to < 0 || to >= _tabs.Count || from == to)
                return false;
            var active = ActiveTab;
            var tab = _tabs[from];
            _tabs.RemoveAt(from);
            _tabs.Insert(to, tab);
            _activeIndex = _tabs.IndexOf(active);
            return true;
        }

        /// <summary>
        /// Closes the tab. Closing the active tab activates its right neighbour
        /// (or the new last tab), matching Explorer. Returns false for an invalid index.
        /// </summary>
        public bool CloseTab(int index)
        {
            if (index < 0 || index >= _tabs.Count) return false;
            var closed = _tabs[index];
            _tabs.RemoveAt(index);
            if (_tabs.Count == 0)
                _activeIndex = -1;
            else if (index < _activeIndex)
                _activeIndex--;
            else if (_activeIndex >= _tabs.Count)
                _activeIndex = _tabs.Count - 1;
            OnTabClosed(closed);
            return true;
        }

        /// <summary>Closes every tab except the given one and the close-exempt (pinned) ones.</summary>
        public void CloseOthers(int index)
        {
            if (index < 0 || index >= _tabs.Count) return;
            var keep = _tabs[index];
            for (int i = _tabs.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(_tabs[i], keep) || IsCloseExempt(_tabs[i])) continue;
                var closed = _tabs[i];
                _tabs.RemoveAt(i);
                OnTabClosed(closed);
            }
            _activeIndex = _tabs.IndexOf(keep);
        }

        /// <summary>Closes the tabs right of the given one, skipping close-exempt (pinned) ones.</summary>
        public void CloseToRight(int index)
        {
            if (index < 0 || index >= _tabs.Count) return;
            var active = ActiveTab;
            for (int i = _tabs.Count - 1; i > index; i--)
            {
                if (IsCloseExempt(_tabs[i])) continue;
                var closed = _tabs[i];
                _tabs.RemoveAt(i);
                OnTabClosed(closed);
            }
            _activeIndex = _tabs.IndexOf(active);
            if (_activeIndex < 0) _activeIndex = Math.Min(index, _tabs.Count - 1);
        }

        /// <summary>Exempt tabs survive Close Others / Close to the Right (pinned tabs).</summary>
        protected virtual bool IsCloseExempt(TTab tab) => false;

        /// <summary>Called for every tab removed by a close operation (recently-closed history).</summary>
        protected virtual void OnTabClosed(TTab tab) { }
    }
}
