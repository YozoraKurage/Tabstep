using System.Collections.Generic;
using NUnit.Framework;

namespace Yozolab.Tabstep.Tests
{
    public class WasdSelectionTests
    {
        static readonly List<string> Items = new List<string>
        {
            "Assets/A", "Assets/B", "Assets/file1.png", "Assets/file2.png",
        };

        [Test]
        public void NextSelectionPath_StepsForwardAndBackward()
        {
            Assert.AreEqual("Assets/B",
                TabstepProjectWindow.NextSelectionPath(Items, "Assets/A", +1));
            Assert.AreEqual("Assets/B",
                TabstepProjectWindow.NextSelectionPath(Items, "Assets/file1.png", -1));
        }

        [Test]
        public void NextSelectionPath_ClampsAtBothEnds()
        {
            Assert.AreEqual("Assets/A",
                TabstepProjectWindow.NextSelectionPath(Items, "Assets/A", -1));
            Assert.AreEqual("Assets/file2.png",
                TabstepProjectWindow.NextSelectionPath(Items, "Assets/file2.png", +1));
        }

        [Test]
        public void NextSelectionPath_NoSelectionStartsFromTheNearestEnd()
        {
            Assert.AreEqual("Assets/A",
                TabstepProjectWindow.NextSelectionPath(Items, null, +1));
            Assert.AreEqual("Assets/file2.png",
                TabstepProjectWindow.NextSelectionPath(Items, null, -1));
        }

        [Test]
        public void NextSelectionPath_SelectionOutsideTheListStartsFromTheNearestEnd()
        {
            Assert.AreEqual("Assets/A",
                TabstepProjectWindow.NextSelectionPath(Items, "Assets/Other/x.png", +1));
            Assert.AreEqual("Assets/file2.png",
                TabstepProjectWindow.NextSelectionPath(Items, "Assets/Other/x.png", -1));
        }

        [Test]
        public void NextSelectionPath_EmptyListReturnsNull()
        {
            Assert.IsNull(TabstepProjectWindow.NextSelectionPath(new List<string>(), null, +1));
        }
    }
}
