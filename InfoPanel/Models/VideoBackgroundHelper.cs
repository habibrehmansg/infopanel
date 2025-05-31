using InfoPanel.Enums;
using System.Diagnostics;
using System.Threading.Tasks;

namespace InfoPanel.Models
{

    public static class VideoBackgroundHelper
    {
        private static async Task Rotate(string inputFile, string outputFile, Rotation rotation)
        {
            /**
            transpose= 1: Rotates the video 90 degrees clockwise.
            transpose= 2: Rotates the video 90 degrees counterclockwise.
            transpose= 3: Rotates the video 90 degrees clockwise and vertical flip (useful for 270-degree rotation).
            transpose= 0: Rotates the video 90 degrees counterclockwise and vertical flip(useful for 180-degree rotation)
            */

            switch (rotation)
            {
                case Rotation.Rotate90FlipNone:
                    await FFmpegOperation(inputFile, $"-vf \"transpose=1\" -c:a copy", outputFile);
                    break;
                case Rotation.Rotate180FlipNone:
                    await FFmpegOperation(inputFile, "-vf \"transpose=1,transpose=1\" -c:a copy", outputFile);
                    break;
                case Rotation.Rotate270FlipNone:
                    await FFmpegOperation(inputFile, "-vf \"transpose=2\" -c:a copy", outputFile);
                    break;
            }
        }

        public static async Task GenerateWebP(string inputFile, string outputFile)
        {
            var operation = "-vf \"fps=20\" -loop 0 -lossless 0 -compression_level 6 -quality 85 -preset photo";
            await FFmpegOperation(inputFile, operation, outputFile);
        }

        public static async Task GenerateMP4(string inputFile, string outputFile)
        {
            var operation = "-vf \"fps=15,scale='if(gt(iw,ih),if(gt(iw,1920),1920,-1),if(gt(ih,1920),-1,iw))':'if(gt(iw,ih),if(gt(iw,1920),-1,ih),if(gt(ih,1920),1920,-1))''\" -c:v libx264 -pix_fmt yuv420p -movflags +faststart";
            await FFmpegOperation(inputFile, operation, outputFile);
        }

        private static async Task GenerateGif(string inputFile, string outputFile)
        {
            var operation = "-filter_complex \"[0:v] fps=15,scale=iw:-1:flags=lanczos,palettegen [p]; [0:v] fps=15,scale=iw:-1:flags=lanczos [x]; [x][p] paletteuse\"";
            await FFmpegOperation(inputFile, operation, outputFile);
        }

        private static async Task FFmpegOperation(string inputFile, string operation, string outputFile, int timeLimit = 30)
        {
            var ffmpegPath = "ffmpeg.exe";
            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-i \"{inputFile}\" {operation} -t {timeLimit} -an -y \"{outputFile}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            process.OutputDataReceived += (sender, args) => HandleOutput(args.Data);
            process.ErrorDataReceived += (sender, args) => HandleOutput(args.Data);


            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();
        }

        static void HandleOutput(string? data)
        {
            if (!string.IsNullOrWhiteSpace(data))
            {
                Trace.WriteLine($"{data}");
            }
        }
    }
}
