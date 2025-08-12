using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using NAudio.Midi;
using NAudio.Wave;

class MyForm : Form
{
    private PictureBox gifBox;
    private List<Image> images;
    private int currentImageIndex = 0;
    private System.Windows.Forms.Timer slideTimer;
    private System.Windows.Forms.Timer gifTimer;
    private bool showGif = true;

    public MyForm(List<Image> images)
    {
        this.DoubleBuffered = true;
        this.FormBorderStyle = FormBorderStyle.None;
        this.WindowState = FormWindowState.Maximized;
        this.TopMost = true;
        this.BackColor = Color.Black;

        this.images = images;

        gifBox = new PictureBox();
        gifBox.Dock = DockStyle.Fill;
        gifBox.SizeMode = PictureBoxSizeMode.CenterImage;
        gifBox.Image = images.Last();
        gifBox.Visible = true;
        this.Controls.Add(gifBox);

        gifTimer = new System.Windows.Forms.Timer();
        gifTimer.Interval = 4000;
        gifTimer.Tick += (s, e) =>
        {
            gifTimer.Stop();
            showGif = false;
            gifBox.Visible = false;
            this.Invalidate();
        };
        gifTimer.Start();

        slideTimer = new System.Windows.Forms.Timer();
        slideTimer.Interval = 100;
        slideTimer.Tick += (s, e) =>
        {
            if (!showGif)
            {
                currentImageIndex = (currentImageIndex + 1) % (images.Count - 1);
                this.Invalidate();
            }
        };
        slideTimer.Start();

        this.Paint += MyForm_Paint;
    }

    private void MyForm_Paint(object sender, PaintEventArgs e)
    {
        if (showGif) return;

        e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;

        var img = images[currentImageIndex];

        int refWidth = images[0].Width;
        int refHeight = images[0].Height;

        float scaleX = (float)this.ClientSize.Width / refWidth;
        float scaleY = (float)this.ClientSize.Height / refHeight;
        float scale = Math.Min(scaleX, scaleY);

        int drawWidth = (int)(refWidth * scale);
        int drawHeight = (int)(refHeight * scale);

        int x = (this.ClientSize.Width - drawWidth) / 2;
        int y = (this.ClientSize.Height - drawHeight) / 2;

        e.Graphics.DrawImage(img, new Rectangle(x, y, drawWidth, drawHeight));
    }
}

class Program
{
    const int SampleRate = 44100;
    static WaveOutEvent waveOut;
    static BufferedWaveProvider waveProvider;
    static AutoResetEvent playbackStoppedEvent = new AutoResetEvent(false);

    static byte[] GetMetaEventBytes(MetaEvent metaEvent)
    {
        var type = metaEvent.GetType();
        var dataField = type.GetField("data", BindingFlags.NonPublic | BindingFlags.Instance);
        if (dataField != null)
        {
            var data = dataField.GetValue(metaEvent) as byte[];
            return data ?? Array.Empty<byte>();
        }
        return Array.Empty<byte>();
    }

    class TempoChange
    {
        public long TickPosition;
        public int MicrosecondsPerQuarterNote;
    }

    static List<TempoChange> BuildTempoMapWithoutSetTempo(MidiFile midiFile)
    {
        var tempoChanges = new List<TempoChange>();
        int defaultTempo = 500000;

        foreach (var midiEvent in midiFile.Events[0])
        {
            if (midiEvent.CommandCode == MidiCommandCode.MetaEvent)
            {
                var meta = (MetaEvent)midiEvent;
                if (meta.MetaEventType == MetaEventType.SetTempo)
                {
                    var data = GetMetaEventBytes(meta);
                    if (data.Length >= 3)
                    {
                        int tempo = (data[0] << 16) | (data[1] << 8) | data[2];
                        tempoChanges.Add(new TempoChange
                        {
                            TickPosition = midiEvent.AbsoluteTime,
                            MicrosecondsPerQuarterNote = tempo
                        });
                    }
                }
            }
        }

        if (tempoChanges.Count == 0)
        {
            tempoChanges.Add(new TempoChange
            {
                TickPosition = 0,
                MicrosecondsPerQuarterNote = defaultTempo
            });
        }

        return tempoChanges.OrderBy(t => t.TickPosition).ToList();
    }

    static double TicksToSeconds(long tick, List<TempoChange> tempoMap, int ticksPerQuarterNote)
    {
        double seconds = 0;
        for (int i = 0; i < tempoMap.Count; i++)
        {
            var current = tempoMap[i];
            var next = (i + 1 < tempoMap.Count) ? tempoMap[i + 1] : null;

            long startTick = current.TickPosition;
            long endTick = next?.TickPosition ?? tick;

            if (tick < startTick)
                break;

            long ticksInSegment = Math.Min(tick, endTick) - startTick;
            seconds += ticksInSegment * (current.MicrosecondsPerQuarterNote / 1000000.0) / ticksPerQuarterNote;

            if (next != null && tick < next.TickPosition)
                break;
        }
        return seconds;
    }

    static double GetMidiLengthInSeconds(MidiFile midiFile, List<TempoChange> tempoMap)
    {
        int ticksPerQuarterNote = midiFile.DeltaTicksPerQuarterNote;
        long maxTick = midiFile.Events.Max(track => track.Last().AbsoluteTime);
        return TicksToSeconds(maxTick, tempoMap, ticksPerQuarterNote);
    }

    static float[] GenerateAudioBuffer(MidiFile midiFile, List<TempoChange> tempoMap, int totalSamples)
    {
        float[] audioBuffer = new float[totalSamples];

        foreach (var track in midiFile.Events)
        {
            foreach (var midiEvent in track)
            {
                double eventTime = TicksToSeconds(midiEvent.AbsoluteTime, tempoMap, midiFile.DeltaTicksPerQuarterNote);

                if (midiEvent.CommandCode == MidiCommandCode.NoteOn)
                {
                    var noteOn = (NoteOnEvent)midiEvent;
                    if (noteOn.Velocity > 0)
                    {
                        double freq = 440 * Math.Pow(2, (noteOn.NoteNumber - 69) / 12.0) * 1.2;
                        double duration = 0.1;

                        int startSample = (int)(eventTime * SampleRate);
                        int endSample = Math.Min(startSample + (int)(duration * SampleRate), audioBuffer.Length);

                        for (int n = startSample; n < endSample; n++)
                        {
                            double t = (n - startSample) / (double)SampleRate;
                            audioBuffer[n] += (float)(Math.Sin(2 * Math.PI * freq * t) * 0.3);
                        }
                    }
                }
            }
        }
        return audioBuffer;
    }
    public static float Clamp(float value, float min, float max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    static byte[] FloatArrayToByteArray(float[] floats)
    {
        byte[] byteBuffer = new byte[floats.Length * 2];
        for (int i = 0; i < floats.Length; i++)
        {
            float sample = Clamp(floats[i], -1f, 1f);
            short intSample = (short)(sample * short.MaxValue);
            byteBuffer[2 * i] = (byte)(intSample & 0xff);
            byteBuffer[2 * i + 1] = (byte)((intSample >> 8) & 0xff);
        }
        return byteBuffer;
    }

    static void PlayMidi(Stream midiStream)
    {
        if (midiStream.CanSeek)
            midiStream.Position = 0;

        var midiFile = new MidiFile(midiStream, false);
        var tempoMap = BuildTempoMapWithoutSetTempo(midiFile);
        double totalSeconds = GetMidiLengthInSeconds(midiFile, tempoMap) * 1.2;
        int totalSamples = (int)(totalSeconds * SampleRate);

        using (waveOut = new WaveOutEvent())
        {
            waveProvider = new BufferedWaveProvider(new WaveFormat(SampleRate, 16, 1))
            {
                BufferLength = totalSamples * 2
            };

            waveOut.Init(waveProvider);

            var playbackStoppedEvent = new AutoResetEvent(false);
            waveOut.PlaybackStopped += (s, e) => playbackStoppedEvent.Set();

            float[] audioBuffer = GenerateAudioBuffer(midiFile, tempoMap, totalSamples);
            byte[] byteBuffer = FloatArrayToByteArray(audioBuffer);

            waveProvider.AddSamples(byteBuffer, 0, byteBuffer.Length);

            waveOut.Play();

            playbackStoppedEvent.WaitOne();
        }
    }

    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Assembly assembly = Assembly.GetExecutingAssembly();
        List<Image> images = new List<Image>();
        string baseNamespace = "ConsoleApp4";

        for (int i = 0; i <= 11; i++)
        {
            string resourceName = $"{baseNamespace}.{i:00}.png";
            using (var stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream != null)
                {
                    using (var ms = new MemoryStream())
                    {
                        stream.CopyTo(ms);
                        ms.Position = 0;
                        images.Add(Image.FromStream(ms));
                    }
                }
            }
        }

        using (var pngStream = assembly.GetManifestResourceStream($"{baseNamespace}.12.png"))
        {
            if (pngStream != null)
            {
                using (var ms = new MemoryStream())
                {
                    pngStream.CopyTo(ms);
                    ms.Position = 0;
                    images.Add(Image.FromStream(ms));
                }
            }
            else
            {
                MessageBox.Show("yourmom", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
        }

        var midiStream = assembly.GetManifestResourceStream("ConsoleApp4.nyan.mid");
        if (midiStream == null)
        {
            MessageBox.Show("MIDI is so fuking old", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        Thread midiThread = new Thread(() =>
        {
            var assemblyInner = Assembly.GetExecutingAssembly();
            var midiStreamInner = assemblyInner.GetManifestResourceStream("ConsoleApp4.nyan.mid");
            if (midiStreamInner == null)
                return;

            while (true)
            {
                using (var msCopy = new MemoryStream())
                {
                    midiStreamInner.Position = 0;
                    midiStreamInner.CopyTo(msCopy);
                    msCopy.Position = 0;
                    PlayMidi(msCopy);
                }
            }
        });
        midiThread.IsBackground = true;
        midiThread.Start();

        MyForm form = new MyForm(images)
        {
            KeyPreview = true
        };

        form.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Escape)
                Application.Exit();

            if (e.Alt && e.KeyCode == Keys.F4)
                e.Handled = true;
        };

        Cursor.Hide();
        form.FormClosed += (s, e) => Cursor.Show();

        form.FormClosing += (s, e) =>
        {
            e.Cancel = true;
        };

        Application.Run(form);

        foreach (var img in images)
            img.Dispose();
    }
}
