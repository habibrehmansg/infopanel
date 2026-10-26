using System;

namespace InfoPanel.Drawing
{
    /// <summary>
    /// Where a visible item is drawn, in profile coordinates: the rectangle (<paramref name="X"/>, <paramref name="Y"/>,
    /// <paramref name="W"/>, <paramref name="H"/>) rotated by <paramref name="Rotation"/> degrees (clockwise) about its centre.
    /// A group (<paramref name="IsGroup"/>) is the axis-aligned union of its visible children, with no rotation.
    /// </summary>
    public readonly record struct ItemBounds(Guid Id, float X, float Y, float W, float H, float Rotation, bool IsGroup);
}
