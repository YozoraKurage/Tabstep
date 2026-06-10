using NUnit.Framework;

namespace Yozolab.Tabstep.Tests
{
    public class ProjectPathsTests
    {
        [TestCase(null, null)]
        [TestCase("", null)]
        [TestCase("/", null)]
        [TestCase("Assets", "Assets")]
        [TestCase("Assets/", "Assets")]
        [TestCase("Assets\\Sub\\Deep", "Assets/Sub/Deep")]
        public void Normalize(string input, string expected)
        {
            Assert.AreEqual(expected, ProjectPaths.Normalize(input));
        }

        [TestCase("Assets", null)]
        [TestCase("Packages", null)]
        [TestCase("Assets/Sub", "Assets")]
        [TestCase("Assets/Sub/Deep/", "Assets/Sub")]
        [TestCase("Packages/com.example.pkg/Runtime", "Packages/com.example.pkg")]
        public void GetParent(string input, string expected)
        {
            Assert.AreEqual(expected, ProjectPaths.GetParent(input));
        }

        [TestCase("Assets", "Assets")]
        [TestCase("Assets/Sub/Deep", "Deep")]
        [TestCase("Assets/Sub/", "Sub")]
        [TestCase(null, null)]
        public void GetDisplayName(string input, string expected)
        {
            Assert.AreEqual(expected, ProjectPaths.GetDisplayName(input));
        }

        [Test]
        public void GetBreadcrumbs_CumulativePathsRootFirst()
        {
            var crumbs = ProjectPaths.GetBreadcrumbs("Assets/Sub/Deep");
            Assert.AreEqual(3, crumbs.Count);
            Assert.AreEqual(("Assets", "Assets"), crumbs[0]);
            Assert.AreEqual(("Sub", "Assets/Sub"), crumbs[1]);
            Assert.AreEqual(("Deep", "Assets/Sub/Deep"), crumbs[2]);
        }

        [Test]
        public void GetBreadcrumbs_NullPath_IsEmpty()
        {
            Assert.IsEmpty(ProjectPaths.GetBreadcrumbs(null));
        }

        [TestCase("Assets/Foo", "Assets/Foo")]
        [TestCase("  Assets/Foo  ", "Assets/Foo")]
        [TestCase("Assets/Foo/", "Assets/Foo")]
        [TestCase("Assets", "Assets")]
        [TestCase("Packages/com.example.pkg/Runtime", "Packages/com.example.pkg/Runtime")]
        [TestCase("AssetsFoo", null)]
        [TestCase("PackagesFoo/Bar", null)]
        [TestCase("C:/proj/Assets/Foo", "Assets/Foo")]
        [TestCase("C:\\proj\\Assets\\Foo", "Assets/Foo")]
        [TestCase("\"C:\\proj\\Assets\\Foo\"", "Assets/Foo")] // Explorer "Copy as path" quotes
        [TestCase("c:/PROJ/Assets/Foo", "Assets/Foo")] // drive/root casing is OS-dependent
        [TestCase("C:/proj/Assets", "Assets")]
        [TestCase("C:/proj/Library/Foo", null)] // inside the project but not browsable
        [TestCase("C:/elsewhere/Assets/Foo", null)]
        [TestCase("C:/proj", null)]
        [TestCase(null, null)]
        [TestCase("", null)]
        [TestCase("\"\"", null)]
        public void ToProjectPath(string input, string expected)
        {
            Assert.AreEqual(expected, ProjectPaths.ToProjectPath(input, "C:/proj"));
        }

        [Test]
        public void ToProjectPath_NullRoot_StillAcceptsRelativePaths()
        {
            Assert.AreEqual("Assets/Foo", ProjectPaths.ToProjectPath("Assets/Foo", null));
            Assert.IsNull(ProjectPaths.ToProjectPath("C:/proj/Assets/Foo", null));
        }

        [Test]
        public void ToProjectPath_UnixStyleRoot()
        {
            Assert.AreEqual("Assets/Foo",
                ProjectPaths.ToProjectPath("/Users/me/proj/Assets/Foo", "/Users/me/proj"));
        }

        [TestCase("Folder", 20, "Folder")]
        [TestCase("AVeryLongFolderName", 8, "AVeryLo…")]
        [TestCase(null, 8, null)]
        public void Ellipsize(string input, int max, string expected)
        {
            Assert.AreEqual(expected, ProjectPaths.Ellipsize(input, max));
        }
    }
}
