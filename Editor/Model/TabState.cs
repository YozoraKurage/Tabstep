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

        public TabState() { }

        public TabState(string folderPath)
        {
            Navigate(folderPath);
        }

        public string CurrentPath => _index >= 0 && _index < _history.Count ? _history[_index] : null;
        public bool CanGoBack => _index > 0;
        public bool CanGoForward => _index < _history.Count - 1;
        public IReadOnlyList<string> History => _history;

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

        public TabState Clone()
        {
            return new TabState
            {
                _history = new List<string>(_history),
                _index = _index,
            };
        }
    }
}
