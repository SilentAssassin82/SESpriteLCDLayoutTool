using Microsoft.VisualStudio.TestTools.UnitTesting;
using SESpriteLCDLayoutTool.Models;
using SESpriteLCDLayoutTool.Models.Rig;
using SESpriteLCDLayoutTool.Services;

namespace SESpriteLCDLayoutTool.Tests
{
    /// <summary>
    /// Regression coverage: the undo manager must capture and restore <see cref="LcdLayout.Rigs"/>.
    /// Without this, any sprite edit followed by undo would silently wipe rig data.
    /// </summary>
    [TestClass]
    public class UndoManagerRigTests
    {
        private static LcdLayout MakeLayoutWithRig()
        {
            var layout = new LcdLayout();
            layout.Sprites.Add(new SpriteEntry { X = 10f, Y = 20f });
            var rig = new Rig
            {
                Id = "rig-1",
                Name = "Test Rig",
                OriginX = 100f,
                OriginY = 200f,
                Bones = { new Bone { Id = "root", LocalX = 5f, LocalRotation = 1.5f, Length = 40f } },
                Bindings = { new SpriteBinding { BoneId = "root", SpriteIndex = 0, OffsetX = 7f } },
            };
            layout.Rigs.Add(rig);
            return layout;
        }

        [TestMethod]
        public void Undo_Restores_Rigs_After_Sprite_Edit()
        {
            var layout = MakeLayoutWithRig();
            var undo = new UndoManager();

            undo.PushUndo(layout);

            // Mutate sprite AND wipe rigs (worst-case caller bug).
            layout.Sprites[0].X = 999f;
            layout.Rigs.Clear();

            Assert.IsTrue(undo.Undo(layout));

            Assert.AreEqual(1, layout.Rigs.Count, "rig restored");
            Assert.AreEqual("rig-1", layout.Rigs[0].Id);
            Assert.AreEqual(100f, layout.Rigs[0].OriginX);
            Assert.AreEqual(1, layout.Rigs[0].Bones.Count);
            Assert.AreEqual("root", layout.Rigs[0].Bones[0].Id);
            Assert.AreEqual(40f, layout.Rigs[0].Bones[0].Length);
            Assert.AreEqual(1, layout.Rigs[0].Bindings.Count);
            Assert.AreEqual(7f, layout.Rigs[0].Bindings[0].OffsetX);
            Assert.AreEqual(10f, layout.Sprites[0].X, "sprite x reverted too");
        }

        [TestMethod]
        public void Snapshot_Is_Deep_Cloned_Not_Shared()
        {
            var layout = MakeLayoutWithRig();
            var undo = new UndoManager();

            undo.PushUndo(layout);

            // Mutate the live rig in place; snapshot must NOT see this.
            layout.Rigs[0].Bones[0].LocalX = 12345f;

            Assert.IsTrue(undo.Undo(layout));
            Assert.AreEqual(5f, layout.Rigs[0].Bones[0].LocalX,
                "snapshot must hold an independent copy");
        }

        [TestMethod]
        public void Redo_Reapplies_Rig_Changes()
        {
            var layout = MakeLayoutWithRig();
            var undo = new UndoManager();

            undo.PushUndo(layout);
            layout.Rigs[0].OriginX = 555f;

            undo.Undo(layout);
            Assert.AreEqual(100f, layout.Rigs[0].OriginX);

            undo.Redo(layout);
            Assert.AreEqual(555f, layout.Rigs[0].OriginX);
        }
    }
}
