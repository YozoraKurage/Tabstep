// Parked: the tabbed inspector is temporarily withdrawn from the Unity UI while its
// integration strategy is reworked in a separate scope. Add TABSTEP_INSPECTOR to the
// project's Scripting Define Symbols to bring it back.
#if TABSTEP_INSPECTOR
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Yozolab.Tabstep.Tests
{
    public class InspectorTabSessionTests
    {
        readonly List<ScriptableObject> _objects = new List<ScriptableObject>();

        ScriptableObject NewObject(string name)
        {
            var obj = ScriptableObject.CreateInstance<ScriptableObject>();
            obj.name = name;
            _objects.Add(obj);
            return obj;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var obj in _objects)
                if (obj != null)
                    Object.DestroyImmediate(obj);
            _objects.Clear();
        }

        [Test]
        public void OpenTab_AppendsAndActivates()
        {
            var session = new InspectorTabSession();
            session.OpenTab(NewObject("A"));
            var b = NewObject("B");
            session.OpenTab(b);

            Assert.AreEqual(2, session.Count);
            Assert.AreEqual(1, session.ActiveIndex);
            Assert.AreSame(b, session.ActiveTab.Target);
        }

        [Test]
        public void OpenOrFocusTab_ExistingTarget_ActivatesInsteadOfDuplicating()
        {
            var session = new InspectorTabSession();
            var a = NewObject("A");
            session.OpenTab(a);
            session.OpenTab(NewObject("B"));

            var tab = session.OpenOrFocusTab(a);

            Assert.AreEqual(2, session.Count);
            Assert.AreEqual(0, session.ActiveIndex);
            Assert.AreSame(session.Tabs[0], tab);
        }

        [Test]
        public void OpenOrFocusTab_NewTarget_OpensTab()
        {
            var session = new InspectorTabSession();
            session.OpenTab(NewObject("A"));
            var b = NewObject("B");

            session.OpenOrFocusTab(b);

            Assert.AreEqual(2, session.Count);
            Assert.AreSame(b, session.ActiveTab.Target);
        }

        [Test]
        public void OpenOrFocusTab_Null_IsNoOp()
        {
            var session = new InspectorTabSession();
            Assert.IsNull(session.OpenOrFocusTab(null));
            Assert.AreEqual(0, session.Count);
        }

        [Test]
        public void Tab_DestroyedTarget_ReportsNotAlive()
        {
            var session = new InspectorTabSession();
            var a = NewObject("A");
            var tab = session.OpenTab(a);
            Object.DestroyImmediate(a);

            Assert.IsFalse(tab.IsAlive);
            Assert.AreEqual("(missing)", tab.DisplayName);
        }

        [Test]
        public void DuplicateTab_InsertsCopyAfterSource()
        {
            var session = new InspectorTabSession();
            var a = NewObject("A");
            session.OpenTab(a);
            session.OpenTab(NewObject("B"));

            var copy = session.DuplicateTab(0);

            Assert.AreEqual(3, session.Count);
            Assert.AreEqual(1, session.ActiveIndex);
            Assert.AreSame(a, copy.Target);
            Assert.AreNotSame(session.Tabs[0], copy);
        }

        [Test]
        public void EnsureSelectionTab_InsertsLeftmostOnce()
        {
            var session = new InspectorTabSession();
            session.OpenTab(NewObject("A"));

            var selection = session.EnsureSelectionTab();
            Assert.AreEqual(0, session.SelectionTabIndex);
            Assert.AreEqual(2, session.Count);
            Assert.IsTrue(selection.FollowsSelection);
            Assert.AreSame(selection, session.ActiveTab);

            session.Activate(1);
            Assert.AreSame(selection, session.EnsureSelectionTab()); // idempotent, re-activates
            Assert.AreEqual(2, session.Count);
            Assert.AreEqual(0, session.ActiveIndex);
        }

        [Test]
        public void CloseSelectionTab_RemovesOnlyThePinnedTab()
        {
            var session = new InspectorTabSession();
            session.EnsureSelectionTab();
            session.OpenTab(NewObject("A"));

            Assert.IsTrue(session.CloseSelectionTab());
            Assert.AreEqual(1, session.Count);
            Assert.AreEqual(-1, session.SelectionTabIndex);
            Assert.IsFalse(session.CloseSelectionTab());
        }

        [Test]
        public void OpenOrFocusTab_IgnoresSelectionTab()
        {
            var session = new InspectorTabSession();
            session.EnsureSelectionTab();
            var a = NewObject("A");
            UnityEditor.Selection.activeObject = a; // selection tab now "shows" A

            try
            {
                session.OpenOrFocusTab(a);
                // A locked tab must be created even though the selection tab matches A.
                Assert.AreEqual(2, session.Count);
                Assert.IsFalse(session.ActiveTab.FollowsSelection);
                Assert.AreSame(a, session.ActiveTab.Target);
            }
            finally
            {
                UnityEditor.Selection.activeObject = null;
            }
        }

        [Test]
        public void SelectionTab_CloneFreezesCurrentSelection()
        {
            var a = NewObject("A");
            UnityEditor.Selection.activeObject = a;
            try
            {
                var selection = InspectorTab.CreateSelectionTab();
                var frozen = selection.Clone();
                Assert.IsFalse(frozen.FollowsSelection);
                Assert.AreSame(a, frozen.Target);
            }
            finally
            {
                UnityEditor.Selection.activeObject = null;
            }
        }

        [Test]
        public void CloseTab_BehavesLikeTabCollection()
        {
            var session = new InspectorTabSession();
            session.OpenTab(NewObject("A"));
            var b = NewObject("B");
            session.OpenTab(b);
            session.OpenTab(NewObject("C"));
            session.Activate(1);

            session.CloseTab(1);

            Assert.AreEqual(2, session.Count);
            Assert.AreEqual("C", session.ActiveTab.Target.name);
        }
    }
}
#endif
