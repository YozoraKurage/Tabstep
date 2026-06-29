using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Yozolab.Tabstep.Tests
{
    public class AssetCreationBridgeTests
    {
        // The bridge is keyed by EditorWindow; ScriptableObject.CreateInstance gives us a
        // throwaway one the test owns so concurrent tests do not collide on a real window.
        static EditorWindow MakeOwner() =>
            ScriptableObject.CreateInstance<DummyOwner>();

        static AssetCreationBridge.Request MakeRequest(string path = "Assets/Foo.cs") =>
            new AssetCreationBridge.Request { InstanceID = 1, PathName = path };

        [Test]
        public void Take_WithoutSubmit_ReturnsNull()
        {
            var owner = MakeOwner();
            try
            {
                Assert.IsNull(AssetCreationBridge.Take(owner));
            }
            finally { Object.DestroyImmediate(owner); }
        }

        [Test]
        public void Submit_Then_Take_ReturnsTheRequest()
        {
            var owner = MakeOwner();
            var req = MakeRequest();
            try
            {
                AssetCreationBridge.Submit(owner, req);
                Assert.AreSame(req, AssetCreationBridge.Take(owner));
            }
            finally { Object.DestroyImmediate(owner); }
        }

        [Test]
        public void Take_IsSingleUse_SecondCallReturnsNull()
        {
            var owner = MakeOwner();
            try
            {
                AssetCreationBridge.Submit(owner, MakeRequest());
                AssetCreationBridge.Take(owner);
                Assert.IsNull(AssetCreationBridge.Take(owner));
            }
            finally { Object.DestroyImmediate(owner); }
        }

        [Test]
        public void Submit_DoubleSubmit_LastOneWins()
        {
            var owner = MakeOwner();
            var first = MakeRequest("Assets/First.cs");
            var second = MakeRequest("Assets/Second.cs");
            try
            {
                AssetCreationBridge.Submit(owner, first);
                AssetCreationBridge.Submit(owner, second);
                Assert.AreSame(second, AssetCreationBridge.Take(owner));
            }
            finally { Object.DestroyImmediate(owner); }
        }

        [Test]
        public void Submit_IsKeyedByOwner_OtherWindowSeesNothing()
        {
            var a = MakeOwner();
            var b = MakeOwner();
            try
            {
                AssetCreationBridge.Submit(a, MakeRequest());
                Assert.IsNull(AssetCreationBridge.Take(b));
                Assert.IsNotNull(AssetCreationBridge.Take(a));
            }
            finally
            {
                Object.DestroyImmediate(a);
                Object.DestroyImmediate(b);
            }
        }

        [Test]
        public void Discard_DropsPendingRequest()
        {
            var owner = MakeOwner();
            try
            {
                AssetCreationBridge.Submit(owner, MakeRequest());
                AssetCreationBridge.Discard(owner);
                Assert.IsNull(AssetCreationBridge.Take(owner));
            }
            finally { Object.DestroyImmediate(owner); }
        }

        [Test]
        public void Submit_NullOwner_NoThrow()
        {
            Assert.DoesNotThrow(() => AssetCreationBridge.Submit(null, MakeRequest()));
        }

        [Test]
        public void Submit_NullRequest_NoThrow()
        {
            var owner = MakeOwner();
            try
            {
                Assert.DoesNotThrow(() => AssetCreationBridge.Submit(owner, null));
                Assert.IsNull(AssetCreationBridge.Take(owner));
            }
            finally { Object.DestroyImmediate(owner); }
        }

        sealed class DummyOwner : EditorWindow { }
    }
}
