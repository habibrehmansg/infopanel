// Protocol initialization adapted from itsmylife44/LcdFusion.
// https://github.com/itsmylife44/LcdFusion/blob/main/protocol/ValkyrieHidInitializer.cs
// The 77-command sequence was verified on a Valkyrie V360 under Fedora Linux.
// MIT License
//
// Copyright (c) 2026 itsmylife44
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System;

namespace InfoPanel.ValkyriePanel;

/// <summary>V360-specific initialization; other MS9132 panels must not use these timings.</summary>
public static class ValkyrieV360Protocol
{
    public const int Width = 320;
    public const int Height = 240;
    public const int PixelDataLength = Width * Height * 2;
    public const int CommitEveryFrames = 30;

    public static void Initialize(Action<byte[], bool> command)
    {
        command([0xA6, 0x03, 0x03], false);
        command([0xB5, 0xC5, 0x58], true);
        command([0xB6, 0xDE, 0xEE, 0x01], false);
        command([0xA6, 0x07, 0x01, 0x02], false);
        command([0xB5, 0xC4, 0x54], true);
        command([0xA6, 0x05, 0x00], false);
        command([0xB5, 0xC5, 0x55], true);
        command([0xB5, 0xF0, 0x04], true);
        command([0xB6, 0xF0, 0x04, 0x6D], false);
        command([0xF5, 0x00, 0x1F, 0xE0], true);
        command([0xF5, 0x00, 0x1F, 0xE8], true);
        command([0xF5, 0x00, 0x1F, 0xF0], true);
        command([0xF5, 0x00, 0x1F, 0xF8], true);
        command([0xB5, 0xC4, 0x24], true);
        command([0xB5, 0xC6, 0x50], true);
        command([0xB5, 0xC3, 0x0F], true);
        command([0xB5, 0xFF, 0x00], true);
        command([0xB5, 0xF0, 0x00], true);
        command([0xB5, 0x00, 0x31], true);
        command([0xB5, 0x00, 0x30], true);
        command([0xB5, 0x00, 0x32], true);
        command([0xB5, 0x00, 0x32], true);
        command([0xB5, 0xC0, 0x00], true);
        command([0xB5, 0xC0, 0x04], true);
        command([0xB5, 0xC0, 0x08], true);
        command([0xB5, 0xC0, 0x0C], true);
        command([0xB5, 0xC0, 0x10], true);
        command([0xB5, 0xC0, 0x14], true);
        command([0xB5, 0xC0, 0x18], true);
        command([0xB5, 0xC0, 0x1C], true);
        command([0xB5, 0xC0, 0x20], true);
        command([0xB5, 0xC0, 0x24], true);
        command([0xB5, 0xC0, 0x28], true);
        command([0xB5, 0xC0, 0x2C], true);
        command([0xB5, 0xC0, 0x30], true);
        command([0xB5, 0xC0, 0x34], true);
        command([0xB5, 0xC0, 0x38], true);
        command([0xB5, 0xC0, 0x3C], true);
        command([0xB5, 0xC0, 0x40], true);
        command([0xB5, 0xC0, 0x44], true);
        command([0xB5, 0xC0, 0x48], true);
        command([0xB5, 0xC0, 0x4C], true);
        command([0xB5, 0xC0, 0x50], true);
        command([0xB5, 0xC0, 0x54], true);
        command([0xB5, 0xC0, 0x58], true);
        command([0xB5, 0xC0, 0x5C], true);
        command([0xB5, 0xC0, 0x60], true);
        command([0xB5, 0xC0, 0x64], true);
        command([0xB5, 0xC0, 0x68], true);
        command([0xB5, 0xC0, 0x6C], true);
        command([0xB5, 0xC0, 0x70], true);
        command([0xB5, 0xC0, 0x74], true);
        command([0xB5, 0xC0, 0x78], true);
        command([0xB5, 0xC0, 0x7C], true);
        command([0xF5, 0x00, 0x1F, 0xE0], true);
        command([0xF5, 0x00, 0x1F, 0xE8], true);
        command([0xF5, 0x00, 0x1F, 0xF0], true);
        command([0xF5, 0x00, 0x1F, 0xF8], true);
        command([0xB5, 0x00, 0x32], true);
        command([0xA6, 0x05, 0x00], false);
        command([0xB5, 0xC5, 0x55], true);
        command([0xB5, 0xF5, 0x07], true);
        command([0xB6, 0xF5, 0x07, 0x02], false);
        command([0xA6, 0x04, 0x00], false);
        command([0xB5, 0xC5, 0x55], true);
        command([0xB5, 0x00, 0x32], true);
        command([0xA6, 0x03, 0x03], false);
        command([0xB5, 0xC5, 0x58], true);
        command([0xA6, 0x01, 0x01, 0x40, 0x00, 0xF0, 0x22, 0x00], false);
        command([0xB5, 0xC5, 0x55], true);
        command([0xA6, 0x02, 0x94, 0x00, 0x01, 0x40, 0x00, 0xF0], false);
        command([0xB5, 0xC5, 0x57], true);
        command([0xA6, 0x04, 0x01, 0x00], false);
        command([0xB5, 0xC5, 0x55], true);
        command([0xB6, 0xF2, 0x4E, 0x00], false);
        command([0xB5, 0x00, 0x32], true);
        command([0xB5, 0xF2, 0x42, 0x00], true);
    }

    // Start scan-out only after the first complete frame has been buffered.
    public static void PostFrame(Action<byte[], bool> command)
    {
        command([0xB5, 0xF5, 0x07], true);
        command([0xB6, 0xF5, 0x07, 0x00], false);
        command([0xA6, 0x05, 0x01, 0x00], false);
        command([0xB5, 0xC5, 0x55], true);
    }
}
