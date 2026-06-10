using NUnit.Framework;

namespace Yozolab.Tabstep.Tests
{
    public class SearchTokenTests
    {
        [Test]
        public void HasSearchToken_MatchesWholeTokensOnly()
        {
            Assert.IsTrue(TabstepProjectWindow.HasSearchToken("boss t:Prefab", "t:Prefab"));
            Assert.IsTrue(TabstepProjectWindow.HasSearchToken("T:PREFAB", "t:Prefab"));
            Assert.IsFalse(TabstepProjectWindow.HasSearchToken("t:PrefabVariant", "t:Prefab"));
            Assert.IsFalse(TabstepProjectWindow.HasSearchToken("", "t:Prefab"));
            Assert.IsFalse(TabstepProjectWindow.HasSearchToken(null, "t:Prefab"));
        }

        [Test]
        public void ToggleSearchToken_AppendsWhenMissing()
        {
            Assert.AreEqual("boss t:Prefab",
                TabstepProjectWindow.ToggleSearchToken("boss", "t:Prefab"));
            Assert.AreEqual("t:Prefab",
                TabstepProjectWindow.ToggleSearchToken(null, "t:Prefab"));
            Assert.AreEqual("t:Prefab",
                TabstepProjectWindow.ToggleSearchToken("", "t:Prefab"));
        }

        [Test]
        public void ToggleSearchToken_RemovesWhenPresent_CaseInsensitive()
        {
            Assert.AreEqual("boss",
                TabstepProjectWindow.ToggleSearchToken("boss t:Prefab", "t:Prefab"));
            Assert.AreEqual("boss",
                TabstepProjectWindow.ToggleSearchToken("T:PREFAB boss", "t:Prefab"));
            Assert.AreEqual("",
                TabstepProjectWindow.ToggleSearchToken("t:Prefab", "t:Prefab"));
        }

        [Test]
        public void ToggleSearchToken_LeavesOtherTokensAlone()
        {
            Assert.AreEqual("boss l:Enemy",
                TabstepProjectWindow.ToggleSearchToken("boss t:Prefab l:Enemy", "t:Prefab"));
        }
    }
}
