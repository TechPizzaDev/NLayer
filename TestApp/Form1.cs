using System;
using System.Windows.Forms;
using NAudio.Wave;
using System.IO;
using System.Diagnostics;
using NLayer.NAudioSupport;
using System.Threading;

namespace TestApp
{
    public partial class Form1 : Form
    {
        public Form1()
        {
            InitializeComponent();
        }

        private void buttonOpen_Click(object sender, EventArgs e)
        {
            //var ofd = new OpenFileDialog();
            //ofd.Filter = "MP3 Files|*.mp3";
            //if (ofd.ShowDialog() != DialogResult.OK)
            //    return;

            //string sourceFile = ofd.FileName;
            //string sourceFile = @"C:\Users\Michal Piatkowski\Music\Celeste - Old Site (Excerpt).mp3";
            string sourceFile = @"C:\Users\Michal Piatkowski\Music\Hollow Knight - Sealed Vessel - Path of Pain Music.mp3";

            using var sourceReader = new Mp3FileReader(sourceFile, waveFormat => new Mp3FrameDecompressor(waveFormat));

            string matchFile = "celeste match.rawpcm";
            using var matchReader = File.Exists(matchFile) ? new FileStream(matchFile, FileMode.Open) : null;

            using var dst = new MemoryStream(1024 * 1024 * 206);
            byte[] dataBuffer = new byte[1024 * 16];
            byte[] matchBuffer = new byte[dataBuffer.Length];
            int sourceRead;
            while ((sourceRead = sourceReader.Read(dataBuffer, 0, dataBuffer.Length)) > 0)
            {
                dst.Write(dataBuffer, 0, sourceRead);

                if (matchReader != null)
                {
                    int toRead = sourceRead;
                    do
                    {
                        int matchRead = matchReader.Read(matchBuffer, sourceRead - toRead, toRead);
                        if (matchRead == 0)
                            throw new EndOfStreamException();

                        toRead -= matchRead;

                    } while (toRead > 0);

                    if (!dataBuffer.AsSpan(0, sourceRead).SequenceEqual(matchBuffer.AsSpan(0, sourceRead)))
                        throw new Exception("not equal");
                }
            }
            dst.Position = 0;

            Thread.Sleep(500);

            string fileName = Path.GetFileNameWithoutExtension(sourceFile) + ".rawpcm";
            fileName = Path.Combine(Environment.CurrentDirectory, fileName);
            Console.WriteLine("Result Path: " + fileName);

            using var outFs = new FileStream(fileName, FileMode.Create);
            dst.CopyTo(outFs);

            //var p = new Process
            //{
            //    StartInfo = new ProcessStartInfo(fileName)
            //    {
            //        UseShellExecute = true
            //    }
            //};
            //p.Start();
        }
    }
}
