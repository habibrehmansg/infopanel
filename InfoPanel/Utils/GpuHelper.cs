using System;

namespace InfoPanel.Utils
{
    public static class GpuHelper
    {
        private static bool? _isNvidia;

        /// <summary>
        /// True if NVIDIA GPU is present (OpenGL supported).
        /// Used for XAML binding via x:Static.
        /// </summary>
        public static bool IsOpenGLSupported => IsNvidiaGpu();

        /// <summary>
        /// Checks if an NVIDIA GPU is present. OpenGL rendering in InfoPanel uses
        /// WGL_NV_DX_interop which is NVIDIA-only. On AMD/Intel GPUs, enabling
        /// OpenGL causes an AccessViolationException crash.
        /// </summary>
        public static bool IsNvidiaGpu()
        {
            if (_isNvidia.HasValue) return _isNvidia.Value;

            try
            {
                for (int i = 0; i <= 3; i++)
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                        $@"SYSTEM\CurrentControlSet\Control\Class\{{4d36e968-e325-11ce-bfc1-08002be10318}}\{i:D4}");
                    var provider = key?.GetValue("ProviderName") as string;
                    if (provider != null && provider.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                    {
                        _isNvidia = true;
                        return true;
                    }
                }
            }
            catch { }

            _isNvidia = false;
            return false;
        }
    }
}
