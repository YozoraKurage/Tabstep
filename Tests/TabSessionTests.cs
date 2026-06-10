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
    }
}
