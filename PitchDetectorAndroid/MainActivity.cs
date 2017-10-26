﻿using Android.App;
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

        const int windowSize = 4096;
        const int sampleRate = 44100;
        const double maxVal = 2;

        private bool shouldContinue = true;

        private object lockObject = new object();
        private Queue<short[]> bufferQueue = new Queue<short[]>();

        private List<double> timeVector = new List<double>(windowSize);

        private TextView txtNote;

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

        void HandleAudioData()
        {
            while (true)
            {
                short[] buffer = GetBuffer();

                DateTime start = DateTime.Now;

                Complex[] input = new Complex[buffer.Length];

                // TODO: Add gauss window and check out frequency leaking
                // TODO: Investigate filter
                var filter = MathNet.Filtering.IIR.OnlineIirFilter.CreateLowpass(MathNet.Filtering.ImpulseResponse.Finite, sampleRate, 1000, 100);
                input = filter.ProcessSamples(buffer.Select(i => (double)i).ToArray()).Select(o => new Complex(o, 0)).ToArray();

                double[] g_window = MathNet.Numerics.Window.Gauss(windowSize, 1);

                if (input.Length != windowSize)
                    Log.Error("Handle Buffer size", "input buffer not of size window");

                // FFT input buffer and get the right frequencies
                MathNet.Numerics.IntegralTransforms.Fourier.Radix2Forward(input);
                double[] freqs = MathNet.Numerics.IntegralTransforms.Fourier.FrequencyScale(windowSize, sampleRate);

                if (input.Max(i => i.Real) < 10000)     // fourier threshold to reject noise
                    continue;

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

                double max_freq = 1.0 / timeVector[max];

                if (max_freq > 50 && max_freq < 2000)
                {
                    KeyValuePair<string, double> pair = NoteFrequencies.GetNoteByFrequency(max_freq);
                    pair = new KeyValuePair<string, double>(pair.Key, -pair.Value);

                    string characteristic = "OK";

                    int noteIndex = NoteFrequencies._Notes.FindIndex(n => n.Name == pair.Key);
                    Note noteEstimation = NoteFrequencies._Notes[noteIndex]; 
                    Note nextNote = NoteFrequencies._Notes[noteIndex + 1 * Math.Sign(pair.Value)];

                    double threshold = Math.Sign(pair.Value) * (nextNote.Frequency - noteEstimation.Frequency) / 6;
                    double successPercent = (pair.Value / Math.Abs((nextNote.Frequency - noteEstimation.Frequency)) * 100);

                    if (pair.Value > 0)
                        if (max_freq > noteEstimation.Frequency + threshold)           // played note frequency is above the limit.
                            characteristic = "+";
                    else
                        if (max_freq < noteEstimation.Frequency - threshold)           // played note is below the limit
                            characteristic = "-";

                    //if (Math.Sign(pair.Value) * (max_freq - noteEstimation.Frequency) > middle)  // played note is above - below limit
                    //    characteristic = Math.Sign(pair.Value).ToString();


                    RunOnUiThread(() => txtNote.Text = $"Note detected {pair.Key}.\n {characteristic}.\nDifference {successPercent.ToString("N3")}%");
                    Log.Info("Note", $"Note detected {pair.Key},{noteEstimation.Frequency}Hz. Frequency: {max_freq} Hz. {characteristic}. Difference {pair.Value.ToString("N3")}");
                }
            }
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

