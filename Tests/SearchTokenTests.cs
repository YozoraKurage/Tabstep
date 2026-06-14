using NUnit.Framework;

namespace Yozolab.Tabstep.Tests
{
    public class SearchTokenTests
    {
        [Test]
        public void HasSearchToken_MatchesWholeTokensOnly()
        {
            Assert.IsTrue(SearchTokens.HasSearchToken("boss t:Prefab", "t:Prefab"));
            Assert.IsTrue(SearchTokens.HasSearchToken("T:PREFAB", "t:Prefab"));
            Assert.IsFalse(SearchTokens.HasSearchToken("t:PrefabVariant", "t:Prefab"));
            Assert.IsFalse(SearchTokens.HasSearchToken("", "t:Prefab"));
            Assert.IsFalse(SearchTokens.HasSearchToken(null, "t:Prefab"));
        }

        [Test]
        public void ToggleSearchToken_AppendsWhenMissing()
        {
            Assert.AreEqual("boss t:Prefab",
                SearchTokens.ToggleSearchToken("boss", "t:Prefab"));
            Assert.AreEqual("t:Prefab",
                SearchTokens.ToggleSearchToken(null, "t:Prefab"));
            Assert.AreEqual("t:Prefab",
                SearchTokens.ToggleSearchToken("", "t:Prefab"));
        }

        [Test]
        public void ToggleSearchToken_RemovesWhenPresent_CaseInsensitive()
        {
            Assert.AreEqual("boss",
                SearchTokens.ToggleSearchToken("boss t:Prefab", "t:Prefab"));
            Assert.AreEqual("boss",
                SearchTokens.ToggleSearchToken("T:PREFAB boss", "t:Prefab"));
            Assert.AreEqual("",
                SearchTokens.ToggleSearchToken("t:Prefab", "t:Prefab"));
        }

        [Test]
        public void ToggleSearchToken_LeavesOtherTokensAlone()
        {
            Assert.AreEqual("boss l:Enemy",
                SearchTokens.ToggleSearchToken("boss t:Prefab l:Enemy", "t:Prefab"));
        }
    }
}
