using System;

namespace Yozolab.Tabstep
{
    /// <summary>Folder tabs of an Tabstep window — see <see cref="TabCollection{TTab}"/>.</summary>
    [Serializable]
    class TabSession : TabCollection<TabState>
    {
        public TabState OpenTab(string folderPath, bool activate = true)
        {
            return Add(new TabState(folderPath), activate);
        }

        /// <summary>Inserts a copy (including history) right after the source and activates it.</summary>
        public TabState DuplicateTab(int index)
        {
            if (index < 0 || index >= Count) return null;
            return InsertAfter(index, Tabs[index].Clone());
        }
    }
}
