using NUnit.Framework;

namespace Yozolab.Tabstep.Tests
{
    public class TabSessionTests
    {
        static TabSession SessionWith(params string[] folders)
        {
            var session = new TabSession();
            foreach (var folder in folders)
                session.OpenTab(folder);
            return session;
        }

        [Test]
        public void OpenTab_AppendsAndActivates()
        {
            var session = SessionWith("Assets", "Assets/A");
            Assert.AreEqual(2, session.Count);
            Assert.AreEqual(1, session.ActiveIndex);
            Assert.AreEqual("Assets/A", session.ActiveTab.CurrentPath);
        }

        [Test]
        public void OpenTab_WithoutActivate_KeepsActiveTab()
        {
            var session = SessionWith("Assets");
            session.OpenTab("Assets/A", activate: false);
            Assert.AreEqual(0, session.ActiveIndex);
        }

        [Test]
        public void CloseActiveTab_ActivatesRightNeighbour()
        {
            var session = SessionWith("Assets", "Assets/A", "Assets/B");
            session.Activate(1);
            session.CloseTab(1);
            Assert.AreEqual("Assets/B", session.ActiveTab.CurrentPath);
        }

        [Test]
        public void CloseLastActiveTab_ActivatesNewLast()
        {
            var session = SessionWith("Assets", "Assets/A");
            session.CloseTab(1);
            Assert.AreEqual(0, session.ActiveIndex);
            Assert.AreEqual("Assets", session.ActiveTab.CurrentPath);
        }

        [Test]
        public void CloseTab_BeforeActive_KeepsActiveTabSelected()
        {
            var session = SessionWith("Assets", "Assets/A", "Assets/B");
            session.CloseTab(0);
            Assert.AreEqual("Assets/B", session.ActiveTab.CurrentPath);
        }

        [Test]
        public void CloseOnlyTab_LeavesEmptySession()
        {
            var session = SessionWith("Assets");
            Assert.IsTrue(session.CloseTab(0));
            Assert.AreEqual(0, session.Count);
            Assert.IsNull(session.ActiveTab);
        }

        [Test]
        public void CloseTab_InvalidIndex_ReturnsFalse()
        {
            var session = SessionWith("Assets");
            Assert.IsFalse(session.CloseTab(-1));
            Assert.IsFalse(session.CloseTab(1));
        }

        [Test]
        public void CloseOthers_KeepsOnlyGivenTab()
        {
            var session = SessionWith("Assets", "Assets/A", "Assets/B");
            session.CloseOthers(1);
            Assert.AreEqual(1, session.Count);
            Assert.AreEqual("Assets/A", session.ActiveTab.CurrentPath);
        }

        [Test]
        public void CloseToRight_RemovesTrailingTabs()
        {
            var session = SessionWith("Assets", "Assets/A", "Assets/B", "Assets/C");
            session.Activate(3);
            session.CloseToRight(1);
            Assert.AreEqual(2, session.Count);
            Assert.AreEqual(1, session.ActiveIndex);
        }

        [Test]
        public void CycleActive_WrapsBothWays()
        {
            var session = SessionWith("Assets", "Assets/A", "Assets/B");
            session.Activate(2);
            session.CycleActive(1);
            Assert.AreEqual(0, session.ActiveIndex);
            session.CycleActive(-1);
            Assert.AreEqual(2, session.ActiveIndex);
        }

        [Test]
        public void DuplicateTab_InsertsIndependentCopyAfterSource()
        {
            var session = SessionWith("Assets", "Assets/B");
            session.Tabs[0].Navigate("Assets/A");
            var copy = session.DuplicateTab(0);

            Assert.AreEqual(3, session.Count);
            Assert.AreSame(copy, session.ActiveTab);
            Assert.AreEqual("Assets/A", copy.CurrentPath);
            Assert.IsTrue(copy.CanGoBack);

            copy.Navigate("Assets/C");
            Assert.AreEqual("Assets/A", session.Tabs[0].CurrentPath);
        }

        [Test]
        public void Move_Reorders_AndKeepsTheActiveTabActive()
        {
            var session = SessionWith("Assets", "Assets/A", "Assets/B");
            session.Activate(0);
            Assert.IsTrue(session.Move(0, 2));
            CollectionAssert.AreEqual(new[] { "Assets/A", "Assets/B", "Assets" },
                TabPaths(session));
            Assert.AreEqual("Assets", session.ActiveTab.CurrentPath);
            Assert.AreEqual(2, session.ActiveIndex);
        }

        [Test]
        public void MoveTab_PinnedTab_StaysInsidePinnedRegion()
        {
            var session = SessionWith("Assets", "Assets/A", "Assets/B");
            session.SetPinned(0, true);
            session.SetPinned(1, true);
            // Trying to drag the first pinned tab past the unpinned region clamps it.
            Assert.AreEqual(1, session.MoveTab(0, 2));
            Assert.IsTrue(session.Tabs[0].Pinned);
            Assert.IsTrue(session.Tabs[1].Pinned);
            Assert.IsFalse(session.Tabs[2].Pinned);
        }

        [Test]
        public void MoveTab_UnpinnedTab_CannotEnterPinnedRegion()
        {
            var session = SessionWith("Assets", "Assets/A", "Assets/B");
            session.SetPinned(0, true);
            Assert.AreEqual(1, session.MoveTab(2, 0));
            CollectionAssert.AreEqual(new[] { "Assets", "Assets/B", "Assets/A" },
                TabPaths(session));
        }

        [Test]
        public void SetPinned_MovesTheTabToThePinnedRegion()
        {
            var session = SessionWith("Assets", "Assets/A", "Assets/B");
            session.SetPinned(2, true);
            Assert.AreEqual("Assets/B", session.Tabs[0].CurrentPath);
            Assert.IsTrue(session.Tabs[0].Pinned);
            Assert.AreEqual(1, session.PinnedCount);

            session.SetPinned(0, false);
            Assert.IsFalse(session.Tabs[0].Pinned);
            Assert.AreEqual(0, session.PinnedCount);
        }

        [Test]
        public void CloseTab_IsRemembered_AndReopens_WithHistory()
        {
            var session = SessionWith("Assets", "Assets/A");
            session.Tabs[1].Navigate("Assets/A/B");
            session.CloseTab(1);
            Assert.AreEqual(1, session.Count);
            Assert.IsTrue(session.HasClosedTabs);

            var reopened = session.ReopenClosedTab();
            Assert.IsNotNull(reopened);
            Assert.AreSame(reopened, session.ActiveTab);
            Assert.AreEqual("Assets/A/B", reopened.CurrentPath);
            Assert.IsTrue(reopened.CanGoBack);
            Assert.IsFalse(session.HasClosedTabs);
        }

        [Test]
        public void ReopenClosedTab_WithoutHistory_ReturnsNull()
        {
            var session = SessionWith("Assets");
            Assert.IsNull(session.ReopenClosedTab());
        }

        [Test]
        public void ReopenClosedTab_NeverComesBackPinned()
        {
            var session = SessionWith("Assets", "Assets/A");
            session.SetPinned(1, true); // moves the pinned tab to index 0
            session.CloseTab(0); // explicit close works even on a pinned tab
            var reopened = session.ReopenClosedTab();
            Assert.IsFalse(reopened.Pinned);
        }

        [Test]
        public void RecentlyClosed_IsCapped()
        {
            var session = SessionWith("Assets");
            for (int i = 0; i < TabSession.MaxClosedTabs + 5; i++)
            {
                session.OpenTab($"Assets/F{i}");
                session.CloseTab(session.Count - 1);
            }
            for (int i = 0; i < TabSession.MaxClosedTabs; i++)
                Assert.IsNotNull(session.ReopenClosedTab());
            Assert.IsNull(session.ReopenClosedTab());
        }

        [Test]
        public void CloseOthers_SparesPinnedTabs()
        {
            var session = SessionWith("Assets", "Assets/A", "Assets/B", "Assets/C");
            session.SetPinned(0, true);
            session.CloseOthers(3);
            CollectionAssert.AreEqual(new[] { "Assets", "Assets/C" }, TabPaths(session));
            Assert.AreEqual("Assets/C", session.ActiveTab.CurrentPath);
        }

        [Test]
        public void CloseToRight_SparesPinnedTabs()
        {
            var session = SessionWith("Assets", "Assets/A", "Assets/B", "Assets/C");
            session.SetPinned(3, true); // moves Assets/C to the front
            session.CloseToRight(1);
            CollectionAssert.AreEqual(new[] { "Assets/C", "Assets" }, TabPaths(session));
        }

        [Test]
        public void DuplicateTab_OfPinnedTab_LandsUnpinnedAfterThePinnedRegion()
        {
            var session = SessionWith("Assets", "Assets/A");
            session.SetPinned(0, true);
            var copy = session.DuplicateTab(0);
            Assert.IsFalse(copy.Pinned);
            Assert.AreEqual(1, session.ActiveIndex);
            Assert.AreEqual(1, session.PinnedCount);
        }

        [Test]
        public void OpenTabAfterActive_InsertsNextToTheActiveTab()
        {
            var session = SessionWith("Assets", "Assets/A", "Assets/B");
            session.Activate(0);
            var opened = session.OpenTabAfterActive("Assets/C");
            Assert.AreSame(opened, session.ActiveTab);
            CollectionAssert.AreEqual(new[] { "Assets", "Assets/C", "Assets/A", "Assets/B" },
                TabPaths(session));
        }

        static string[] TabPaths(TabSession session)
        {
            var paths = new string[session.Count];
            for (int i = 0; i < session.Count; i++)
                paths[i] = session.Tabs[i].CurrentPath;
            return paths;
        }
    }
}
