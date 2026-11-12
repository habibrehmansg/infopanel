// V1 oracle, test-only: the parts of InfoPanel 1.4.x c71f0f00's device tasks that decide bytes, copied verbatim from
// the files and lines cited above each part, but for these changes: log lines are left out; the device is the oracle's
// (V1JonsboMs9132Device, V1VmaxUsbDevice); a rendered bitmap is its RGBA bytes and row length (1.4.x read SKBitmap's
// pixels through a pointer: the same bytes, indexed); the render, retry and pacing loops are the engine's and left out.
// InfoPanel/Services/JonsboPanelDeviceTask.cs and InfoPanel/Services/VmaxPanelDeviceTask.cs.
namespace InfoPanel.Core.Tests.Panels.Ms9132Jonsbo.V1Oracle
{
    public static class V1JonsboPanelDeviceTask
    {
        // JonsboPanelDeviceTask.cs:320-345: the mode the session sets, from the EDID the startup block read
        public static (int Width, int Height, byte Vic) ChooseMode(JonsboPanelModelInfo modelInfo, V1JonsboMs9132Device ms)
        {
            int _panelWidth = modelInfo.Width, _panelHeight = modelInfo.Height;
            var vic = modelInfo.Vic;
            if (ms.EdidResolution is (int edidWidth, int edidHeight)
                && V1JonsboMs9132Device.GetVicForResolution(edidWidth, edidHeight) is byte edidVic)
            {
                _panelWidth = edidWidth;
                _panelHeight = edidHeight;
                vic = edidVic;
            }
            return (_panelWidth, _panelHeight, vic);
        }

        // JonsboPanelDeviceTask.cs:386-394: one frame of the send loop
        public static void SendOne(V1JonsboMs9132Device ms, byte[] bgrBuffer, int width, int height, ref int framesSent)
        {
            ms.SendFrame(bgrBuffer, width, height);
            framesSent++;

            ms.PollPanelStatus();
            if (framesSent == 2) ms.EnableVideo();
        }

        // JonsboPanelDeviceTask.cs:430-460: RGBA to BGR888, dimmed below 100
        public static void GenerateBgrFrame(byte[] rgba, int rowBytes, int width, int height, int brightness, byte[] bgrBuffer)
        {
            float scale = Math.Clamp(brightness, 0, 100) / 100f;
            bool dim = scale < 1f;
            int dst = 0;
            for (int y = 0; y < height; y++)
            {
                int row = y * rowBytes;
                for (int x = 0; x < width; x++)
                {
                    byte r = rgba[row + x * 4];      // Rgba8888: R,G,B,A
                    byte g = rgba[row + x * 4 + 1];
                    byte b = rgba[row + x * 4 + 2];
                    if (dim)
                    {
                        r = (byte)(r * scale);
                        g = (byte)(g * scale);
                        b = (byte)(b * scale);
                    }
                    // Wire format is BGR888 (DRM_FORMAT_RGB888 memory order)
                    bgrBuffer[dst++] = b;
                    bgrBuffer[dst++] = g;
                    bgrBuffer[dst++] = r;
                }
            }
        }
    }

    public static class V1VmaxPanelDeviceTask
    {
        // VmaxPanelDeviceTask.cs:136-138: one frame, then the screen switch
        public static void SendOne(V1VmaxUsbDevice vmaxDevice, byte[] frame, bool screenSwitch)
        {
            vmaxDevice.SendRgb888Frame(frame, frame.Length);

            vmaxDevice.SetScreenSwitch(screenSwitch);
        }

        // VmaxPanelDeviceTask.cs:143: the frame rate, at most 30
        public static int TargetFrameTime(int targetFrameRate) => 1000 / Math.Max(1, Math.Min(targetFrameRate, 30));

        // VmaxPanelDeviceTask.cs:197-222: RGBA to BGR888, the brightness (0 to 99) applied in the conversion
        public static void ToBgr888(byte[] pixels, int srcStride, int width, int height, byte[] output, int brightness)
        {
            var scale = Math.Clamp(brightness, 0, 99);
            int offset = 0;
            for (int y = 0; y < height; y++)
            {
                int srcRow = y * srcStride;
                for (int x = 0; x < width; x++)
                {
                    int si = x * 4;
                    output[offset++] = (byte)(pixels[srcRow + si + 2] * scale / 100);
                    output[offset++] = (byte)(pixels[srcRow + si + 1] * scale / 100);
                    output[offset++] = (byte)(pixels[srcRow + si + 0] * scale / 100);
                }
            }
        }
    }
}
