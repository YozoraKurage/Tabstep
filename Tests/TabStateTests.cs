using NUnit.Framework;

namespace Yozolab.Tabstep.Tests
{
    public class TabStateTests
    {
        [Test]
        public void NewTab_StartsAtGivenFolder()
        {
            var tab = new TabState("Assets/Sub");
            Assert.AreEqual("Assets/Sub", tab.CurrentPath);
            Assert.IsFalse(tab.CanGoBack);
            Assert.IsFalse(tab.CanGoForward);
        }

        [Test]
        public void Navigate_PushesHistory()
        {
            var tab = new TabState("Assets");
            tab.Navigate("Assets/A");
            tab.Navigate("Assets/A/B");
            Assert.AreEqual("Assets/A/B", tab.CurrentPath);
            Assert.IsTrue(tab.CanGoBack);
            Assert.AreEqual(3, tab.History.Count);
        }

        [Test]
        public void Navigate_SameFolder_IsNoOp()
        {
            var tab = new TabState("Assets");
            tab.Navigate("Assets");
            tab.Navigate("Assets/");
            Assert.AreEqual(1, tab.History.Count);
        }

        [Test]
        public void Navigate_NormalizesPath()
        {
            var tab = new TabState("Assets");
            tab.Navigate("Assets\\Sub\\");
            Assert.AreEqual("Assets/Sub", tab.CurrentPath);
        }

        [Test]
        public void GoBack_GoForward_WalkHistory()
        {
            var tab = new TabState("Assets");
            tab.Navigate("Assets/A");
            tab.Navigate("Assets/A/B");

            Assert.AreEqual("Assets/A", tab.GoBack());
            Assert.AreEqual("Assets", tab.GoBack());
            Assert.IsFalse(tab.CanGoBack);
            Assert.AreEqual("Assets", tab.GoBack()); // stays at the oldest entry

            Assert.AreEqual("Assets/A", tab.GoForward());
            Assert.AreEqual("Assets/A/B", tab.GoForward());
            Assert.IsFalse(tab.CanGoForward);
        }

        [Test]
        public void Navigate_AfterGoBack_DiscardsForwardHistory()
        {
            var tab = new TabState("Assets");
            tab.Navigate("Assets/A");
            tab.Navigate("Assets/A/B");
            tab.GoBack();
            tab.Navigate("Assets/C");

            Assert.AreEqual("Assets/C", tab.CurrentPath);
            Assert.IsFalse(tab.CanGoForward);
            CollectionAssert.AreEqual(new[] { "Assets", "Assets/A", "Assets/C" }, tab.History);
        }

        [Test]
        public void History_IsCapped_DroppingOldestEntries()
        {
            var tab = new TabState("Assets");
            for (int i = 0; i < TabState.MaxHistory + 10; i++)
                tab.Navigate($"Assets/F{i}");
            Assert.AreEqual(TabState.MaxHistory, tab.History.Count);
            Assert.AreEqual($"Assets/F{TabState.MaxHistory + 9}", tab.CurrentPath);
            Assert.IsTrue(tab.CanGoBack);
        }

        [Test]
        public void Reset_ClearsHistory()
        {
            var tab = new TabState("Assets");
            tab.Navigate("Assets/A");
            tab.Reset("Assets/B");
            Assert.AreEqual("Assets/B", tab.CurrentPath);
            Assert.IsFalse(tab.CanGoBack);
            Assert.IsFalse(tab.CanGoForward);
        }

        [Test]
        public void Clone_IsIndependent()
        {
            var tab = new TabState("Assets");
            tab.Navigate("Assets/A");
            var copy = tab.Clone();

            copy.Navigate("Assets/B");
            Assert.AreEqual("Assets/A", tab.CurrentPath);
            Assert.AreEqual("Assets/B", copy.CurrentPath);
            Assert.AreEqual(2, tab.History.Count);
        }
    }
}
