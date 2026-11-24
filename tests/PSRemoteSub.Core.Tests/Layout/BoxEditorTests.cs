using PSRemoteSub.Core.Layout;

namespace PSRemoteSub.Core.Tests.Layout;

public class BoxEditorTests
{
    private const double W = 1000, H = 600, MinW = 20, MinH = 10, Grip = 8;
    private static readonly EditRect B = new(100, 100, 200, 50);
    private static readonly EditRect Big = new(50, 50, 600, 300);

    private static EditRect Drag(EditRect start, BoxHandle handle, double dx, double dy) =>
        BoxEditor.Drag(start, handle, dx, dy, W, H, MinW, MinH);

    [Fact]
    public void Hit_Corner()
    {
        Assert.Equal(BoxHandle.NW, BoxEditor.HitHandle(B, 101, 99, Grip));
        Assert.Equal(BoxHandle.SE, BoxEditor.HitHandle(B, 300, 150, Grip));
    }

    [Fact]
    public void Hit_Edge()
    {
        Assert.Equal(BoxHandle.N, BoxEditor.HitHandle(B, 200, 100, Grip));
        Assert.Equal(BoxHandle.E, BoxEditor.HitHandle(B, 300, 125, Grip));
    }

    [Fact] public void Hit_Inside_Move() => Assert.Equal(BoxHandle.Move, BoxEditor.HitHandle(B, 200, 125, Grip));

    [Fact] public void Hit_Outside_None() => Assert.Equal(BoxHandle.None, BoxEditor.HitHandle(B, 50, 50, Grip));

    [Fact] public void Move_ShiftsBox() => Assert.Equal(new EditRect(110, 120, 200, 50), Drag(B, BoxHandle.Move, 10, 20));

    [Fact] public void Move_ClampedInsideBounds() =>
        Assert.Equal(new EditRect(800, 550, 200, 50), Drag(B, BoxHandle.Move, 900, 900));

    [Fact] public void ResizeSE_GrowsRightAndDown() =>
        Assert.Equal(new EditRect(100, 100, 230, 60), Drag(B, BoxHandle.SE, 30, 10));

    [Fact] public void ResizeNW_KeepsOppositeCorner() =>
        Assert.Equal(new EditRect(80, 90, 220, 60), Drag(B, BoxHandle.NW, -20, -10));

    [Fact] public void ResizePastOppositeEdge_StopsAtMinimum() =>
        Assert.Equal(new EditRect(280, 100, 20, 50), Drag(B, BoxHandle.W, 500, 0));

    [Fact] public void ResizeOutOfBounds_Clamped() =>
        Assert.Equal(new EditRect(100, 0, 200, 150), Drag(B, BoxHandle.N, 0, -500));

    [Fact]
    public void Pick_CoincidentHandles_ActiveWins()
    {
        Assert.Equal((EditorBox.Ocr, BoxHandle.NW), BoxEditor.Pick(B, B, EditorBox.Ocr, 100, 100, Grip));
        Assert.Equal((EditorBox.Caption, BoxHandle.NW), BoxEditor.Pick(B, B, EditorBox.Caption, 100, 100, Grip));
    }

    [Fact] public void Pick_BodiesOverlap_InnermostWins() =>
        Assert.Equal((EditorBox.Caption, BoxHandle.Move), BoxEditor.Pick(Big, B, EditorBox.Ocr, 200, 125, Grip));

    [Fact] public void Pick_CaptionEnclosesOcr_OcrBodyStillGrabbable() =>
        Assert.Equal((EditorBox.Ocr, BoxHandle.Move), BoxEditor.Pick(B, Big, EditorBox.Caption, 200, 125, Grip));

    [Fact]
    public void Pick_ExactCopy_ActiveBodyWins()
    {
        Assert.Equal((EditorBox.Ocr, BoxHandle.Move), BoxEditor.Pick(B, B, EditorBox.Ocr, 200, 125, Grip));
        Assert.Equal((EditorBox.Caption, BoxHandle.Move), BoxEditor.Pick(B, B, EditorBox.Caption, 200, 125, Grip));
    }

    [Fact]
    public void DragThreshold_SmallJitterIsAClick()
    {
        Assert.False(BoxEditor.IsDrag(2, -2));
        Assert.True(BoxEditor.IsDrag(3, 0));
        Assert.True(BoxEditor.IsDrag(0, -3));
    }

    [Fact] public void Pick_OtherBoxHandle_BeatsActiveBody() =>
        Assert.Equal((EditorBox.Caption, BoxHandle.SE), BoxEditor.Pick(Big, B, EditorBox.Ocr, 300, 150, Grip));

    [Fact] public void Pick_Empty_ReturnsActiveNone() =>
        Assert.Equal((EditorBox.Caption, BoxHandle.None), BoxEditor.Pick(Big, B, EditorBox.Caption, 900, 550, Grip));

    [Fact] public void Pick_NoBoxes_ReturnsActiveNone() =>
        Assert.Equal((EditorBox.Ocr, BoxHandle.None), BoxEditor.Pick(null, null, EditorBox.Ocr, 200, 125, Grip));

    private static readonly EditRect Mid = new(80, 80, 400, 200);
    private static readonly EditRect Small = new(150, 110, 100, 30);
    private static readonly EditRect Elsewhere = new(700, 400, 200, 100);

    [Fact] public void Pick_ThreeBoxes_TriggerHandleWins() =>
        Assert.Equal((EditorBox.Trigger, BoxHandle.SE), BoxEditor.Pick(
            [(EditorBox.Ocr, Big), (EditorBox.Caption, Elsewhere), (EditorBox.Trigger, B)],
            EditorBox.Trigger, 300, 150, Grip));

    [Fact] public void Pick_SmallestBodyWins_AmongThree() =>
        Assert.Equal((EditorBox.Trigger, BoxHandle.Move), BoxEditor.Pick(
            [(EditorBox.Ocr, Big), (EditorBox.Caption, Mid), (EditorBox.Trigger, Small)],
            EditorBox.Ocr, 200, 125, Grip));

    [Fact] public void Pick_ThreeBoxes_OtherHandlesInListOrder() =>
        Assert.Equal((EditorBox.Caption, BoxHandle.NW), BoxEditor.Pick(
            [(EditorBox.Ocr, Big), (EditorBox.Caption, B), (EditorBox.Trigger, B)],
            EditorBox.Ocr, 100, 100, Grip));

    [Fact] public void Pick_ThreeBoxes_EqualBodies_ActiveWins() =>
        Assert.Equal((EditorBox.Trigger, BoxHandle.Move), BoxEditor.Pick(
            [(EditorBox.Ocr, B), (EditorBox.Caption, B), (EditorBox.Trigger, B)],
            EditorBox.Trigger, 200, 125, Grip));

    [Fact] public void Pick_ThreeBoxes_MissingBoxesIgnored() =>
        Assert.Equal((EditorBox.Trigger, BoxHandle.None), BoxEditor.Pick(
            [(EditorBox.Ocr, null), (EditorBox.Caption, null), (EditorBox.Trigger, null)],
            EditorBox.Trigger, 200, 125, Grip));

    [Fact] public void FromDrag_Normalizes_AndClamps() =>
        Assert.Equal(new EditRect(0, 200, 300, 400), BoxEditor.FromDrag(300, 200, -50, 700, W, H));
}
