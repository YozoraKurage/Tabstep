using System;
using System.Collections.Generic;
using UnityEngine;

namespace Yozolab.Tabstep
{
    /// <summary>
    /// One Explorer-style tab: the folder it currently shows plus back/forward history.
    /// Pure data — serialized into the window so tabs survive domain reloads.
    /// </summary>
    [Serializable]
    class TabState
    {
        // Oldest entries are dropped past this; keeps serialized window state bounded.
        internal const int MaxHistory = 100;

        [SerializeField] List<string> _history = new List<string>();
        [SerializeField] int _index = -1;
        [SerializeField] bool _pinned;
        [SerializeField] string _searchText = "";

        public TabState() { }

        public TabState(string folderPath)
        {
            Navigate(folderPath);
        }

        public string CurrentPath => _index >= 0 && _index < _history.Count ? _history[_index] : null;
        public bool CanGoBack => _index > 0;
        public bool CanGoForward => _index < _history.Count - 1;
        public IReadOnlyList<string> History => _history;
        public int HistoryIndex => _index;

        /// <summary>Pinned tabs sit leftmost, render icon-only and resist accidental closing.</summary>
        public bool Pinned
        {
            get => _pinned;
            set => _pinned = value;
        }

        /// <summary>The tab's own search filter, restored when the tab becomes active again.</summary>
        public string SearchText
        {
            get => _searchText;
            set => _searchText = value ?? "";
        }

        /// <summary>
        /// Enter a folder. Like a web browser, anything ahead of the current position
        /// (forward history) is discarded. Re-navigating to the current folder is a no-op.
        /// </summary>
        public void Navigate(string folderPath)
        {
            folderPath = ProjectPaths.Normalize(folderPath);
            if (folderPath == null || folderPath == CurrentPath) return;
            if (_index < _history.Count - 1)
                _history.RemoveRange(_index + 1, _history.Count - _index - 1);
            _history.Add(folderPath);
            _index = _history.Count - 1;
            if (_history.Count > MaxHistory)
            {
                int excess = _history.Count - MaxHistory;
                _history.RemoveRange(0, excess);
                _index -= excess;
            }
        }

        /// <summary>Replaces the current entry without growing history (used when a folder was deleted).</summary>
        public void Reset(string folderPath)
        {
            folderPath = ProjectPaths.Normalize(folderPath);
            if (folderPath == null) return;
            _history.Clear();
            _history.Add(folderPath);
            _index = 0;
        }

        public string GoBack()
        {
            if (CanGoBack) _index--;
            return CurrentPath;
        }

        public string GoForward()
        {
            if (CanGoForward) _index++;
            return CurrentPath;
        }

        /// <summary>Jumps straight to a history entry (the back/forward dropdown menu).</summary>
        public void GoToHistoryIndex(int index)
        {
            if (index >= 0 && index < _history.Count) _index = index;
        }

        /// <summary>The copy is never pinned: pinning is a property of the tab's slot, not its content.</summary>
        public TabState Clone()
        {
            return new TabState
            {
                _history = new List<string>(_history),
                _index = _index,
                _searchText = _searchText,
            };
        }
    }
}
