using Android.App;
using Android.Widget;
using Android.OS;
using System.Threading;
using Android.Media;
using System.Collections.Generic;
using Android.Util;
using System;
using System.Numerics;
using System.Linq;

namespace PitchDetectorAndroid
{
    [Activity(Label = "PitchDetectorAndroid", MainLauncher = true, Icon = "@mipmap/icon")]
    public class MainActivity : Activity
    {
        Thread audioThread;
        Thread pitchThread;
        AudioRecord record;

        const int windowSize = 2048;
        const int sampleRate = 44100;
        const double maxVal = 2;
        readonly int notesMemory = 5;

        private bool shouldContinue = true;

        private object lockObject = new object();
        private Queue<short[]> bufferQueue = new Queue<short[]>();

        private List<double> timeVector = new List<double>(windowSize);

        private TextView txtNote;

        private List<(Note, double)> notesPlayed = new List<(Note, double)>();

        #region Audio buffer queue sync function

        private void AddBuffer(short[] buffer)
        {
            lock (lockObject)
            {
                bufferQueue.Enqueue(buffer);

                Monitor.Pulse(lockObject);
            }
        }

        private short[] GetBuffer()
        {
            short[] buffer = new short[0];

            lock (lockObject)
            {
                Monitor.Wait(lockObject);

                while (bufferQueue.Count > 0)
                    buffer = bufferQueue.Dequeue();
            }

            return buffer;
        }

        #endregion

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            // Set our view from the "main" layout resource
            SetContentView(Resource.Layout.Main);

            // Get our button from the layout resource,
            // and attach an event to it
            Button button = FindViewById<Button>(Resource.Id.myButton);
            txtNote = FindViewById<TextView>(Resource.Id.txtNoteFreq);

            button.Click += delegate { CreateAudioThread().Start();
                                       CreatePitchThread().Start(); };

            for (int i = 0; i < windowSize; i++)
                timeVector.Add((double)i / sampleRate);

            Window.AddFlags(Android.Views.WindowManagerFlags.KeepScreenOn);
            SeekBar barNoise = FindViewById<SeekBar>(Resource.Id.barNoise);
            barNoise.ProgressChanged += delegate { Log.Info("Progress", $"Progress bar: {barNoise.Progress}"); };
        }

        Thread CreateAudioThread()
        {
            audioThread = new Thread(RecordAudio);
            return audioThread;
        }

        Thread CreatePitchThread()
        {
            pitchThread = new Thread(HandleAudioData);
            return pitchThread;
        }

        (Note, double) DetectPitch(double[] buffer)
        {
            Complex[] input = new Complex[buffer.Length];

            // TODO: Add gauss window and check out frequency leaking
            // TODO: Investigate filter
            var filter = MathNet.Filtering.IIR.OnlineIirFilter.CreateLowpass(MathNet.Filtering.ImpulseResponse.Finite, sampleRate, 1000, 100);
            input = filter.ProcessSamples(buffer).Select(o => new Complex(o, 0)).ToArray();

            double[] g_window = MathNet.Numerics.Window.Gauss(windowSize, 1);

            if (input.Length != windowSize)
            {
                Log.Error("Handle Buffer size", "input buffer not of size window");
                return (Note.EmptyNote, 0);
            }

            // FFT input buffer and get the right frequencies
            MathNet.Numerics.IntegralTransforms.Fourier.Radix2Forward(input);
            double[] freqs = MathNet.Numerics.IntegralTransforms.Fourier.FrequencyScale(windowSize, sampleRate);

            // fourier threshold to reject noise
            if (input.Max(i => i.Real) < FindViewById<SeekBar>(Resource.Id.barNoise).Progress * 10)
                return (Note.EmptyNote, 0);

            // calculate autocorrelation
            Complex[] autocor = new Complex[windowSize];

            for (int i = 0; i < windowSize; i++)
                autocor[i] = input[i] * Complex.Conjugate(input[i]);

            // return to time domain
            MathNet.Numerics.IntegralTransforms.Fourier.Radix2Inverse(autocor);

            // cap autocorrelation values and strengthen early peaks
            double[] result = new double[windowSize];

            for (int i = 0; i < autocor.Length; i++)
            {
                if (i <= 20)
                    result[i] = 0;
                else
                    result[i] = autocor[i].Real *
                                        (maxVal - (1 - maxVal) / (timeVector[0] - timeVector.Last()) * (timeVector[i] - timeVector[0]));
            }

            // calculate autocorrelation maximum
            int max = 0;
            for (int i = 0; i < result.Length / 2 + 1; i++)
                if (result[i] > result[max])
                    max = i;

            if (max - 1 < 0 || max + 1 >= result.Length)
                return (Note.EmptyNote, 0);

            double[] p3 = { 1.0 / timeVector[max - 1], result[max - 1] };
            double[] p2 = { 1.0 / timeVector[max], result[max] };
            double[] p1 = { 1.0 / timeVector[max + 1], result[max + 1] };

            double max_freq = QuadraticMaximum(p1, p2, p3);

            if (max_freq > 50 && max_freq < 2000)
            {
                KeyValuePair<string, double> pair = NoteFrequencies.GetNoteByFrequency(max_freq);
                pair = new KeyValuePair<string, double>(pair.Key, -pair.Value);

                
                int noteIndex = NoteFrequencies._Notes.FindIndex(n => n.Name == pair.Key);
                Note noteEstimation = NoteFrequencies._Notes[noteIndex];               

                //if (Math.Sign(pair.Value) * (max_freq - noteEstimation.Frequency) > threshold)  // played note is above - below limit
                //    characteristic = Math.Sign(pair.Value).ToString();
                return (noteEstimation, max_freq);
            }

            return (Note.EmptyNote, 0);
        }

        void HandleAudioData()
        {
            while (true)
            {
                short[] temp = GetBuffer();
                double[] buffer = temp.Select(i => (double)i).ToArray();

                var median = MathNet.Filtering.Median.OnlineMedianFilter.CreateDenoise(7);
                buffer = median.ProcessSamples(buffer);

                var result = DetectPitch(buffer);

                Note noteEstimation = result.Item1;
                double actualFreq = result.Item2;
                double difference = actualFreq - noteEstimation.Frequency;

                string characteristic = "OK";
                if (noteEstimation.Frequency == 0)
                {
                    ClearNotes();
                    continue;
                }

                int noteIndex = NoteFrequencies._Notes.FindIndex(n => n.Name == noteEstimation.Name);
                Note nextNote = NoteFrequencies._Notes[noteIndex + 1 * Math.Sign(difference)];

                double threshold = Math.Sign(difference) * (nextNote.Frequency - noteEstimation.Frequency) / 6;
                double successPercent = (difference / Math.Abs((nextNote.Frequency - noteEstimation.Frequency)) * 100);

                if (difference > 0)
                    if (actualFreq > noteEstimation.Frequency + threshold)           // played note frequency is above the limit.
                        characteristic = "+";
                else
                    if (actualFreq < noteEstimation.Frequency - threshold)           // played note is below the limit
                        characteristic = "-";

                // when the note vector reaches the given memory size, calculate median and print results
                if(AppendNote((noteEstimation, actualFreq)))
                {
                    double meanFreq = GetNotesMedian();
                    ClearNotes();
                    RunOnUiThread(() => txtNote.Text = $"Note detected {noteEstimation.Name} {meanFreq.ToString("N1")}.\n {characteristic}.\nDifference {successPercent.ToString("N3")}%");
                }
            }
        }

        double QuadraticMaximum(double[] p1, double[] p2, double[] p3)
        {
            double a = p1[1] / ((p1[0] - p2[0]) * (p1[0] - p3[0])) + p2[1] / ((p2[0] - p1[0]) * (p2[0] - p3[0])) + p3[1] / ((p3[0] - p1[0]) * (p3[0] - p2[0]));
            if (a == 0)
                return 0;
            double b = -p1[1] * (p2[0] + p3[0]) / ((p1[0] - p2[0]) * (p1[0] - p3[0])) - p2[1] * (p3[0] + p1[0]) / ((p2[0] - p1[0]) * (p2[0] - p3[0])) - p3[1] * (p1[0] + p2[0]) / ((p3[0] - p1[0]) * (p3[0] - p2[0]));
            double c = p1[1] * p2[0] * p3[0] / ((p1[0] - p2[0]) * (p1[0] - p3[0])) + p2[1] * p3[0] * p1[0] / ((p2[0] - p1[0]) * (p2[0] - p3[0])) + p3[1] * p1[0] * p2[0] / ((p3[0] - p1[0]) * (p3[0] - p2[0]));
            double max_val = -b / (2 * a);
            return max_val;
        }

        bool AppendNote((Note, double) n)
        {
            notesPlayed.Add(n);

            if (notesPlayed.Count > notesMemory)
                return true;

            return false;
        }

        void ClearNotes()
        {
            notesPlayed.Clear();
        }

        double GetNotesMedian()
        {
            notesPlayed.Sort((n1, n2) => n1.Item2 > n2.Item2 ? 1 : -1);

            if (notesMemory % 2 == 0)
                return (notesPlayed[notesMemory / 2].Item2 + notesPlayed[notesMemory / 2 + 1].Item2) / 2;
            else
                return notesPlayed[notesPlayed.Count / 2 + 1].Item2;
        }
       

        void RecordAudio()
        {
            Android.OS.Process.SetThreadPriority(Android.OS.ThreadPriority.Audio);

            int bufferSize = AudioRecord.GetMinBufferSize(sampleRate, ChannelIn.Mono, Encoding.Pcm16bit);


            if (bufferSize <= 0 || bufferSize >= ushort.MaxValue)       // cap buffer size
                bufferSize = sampleRate * 2;

            bufferSize = windowSize;        // Attention to this line of code
            short[] audioBuffer = new short[bufferSize];

            record = new AudioRecord(AudioSource.Default, sampleRate, ChannelIn.Mono, Encoding.Pcm16bit, bufferSize);

            if (record.State != State.Initialized)
            {
                Log.Error("Audio Error", "Audio Record can't initialize");
                return;
            }

            record.StartRecording();

            long shortsRead = 0;
            while (shouldContinue)
            {
                int numberOfShort = record.Read(audioBuffer, 0, audioBuffer.Length);
                shortsRead += numberOfShort;
                AddBuffer(audioBuffer);
            }

            record.Stop();
            record.Release();
        }
    }
}

